using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Wf.Contracts.Secrets;

namespace Wf.Sdk.Hosting;

internal sealed class TaskOutputCapture : IAsyncDisposable
{
    private readonly TextWriter originalOutput = Console.Out;
    private readonly TextWriter originalError = Console.Error;
    private readonly CapturedStream output;
    private readonly CapturedStream error;
    private bool restored;

    internal TaskOutputCapture(AttemptSpool spool, SecretRedactor redactor)
    {
        output = new(1, "stdout", spool, redactor);
        try { error = new(2, "stderr", spool, redactor); }
        catch { output.Restore(); output.Dispose(); throw; }
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { Restore(); output.Dispose(); error.Dispose(); throw; }
    }

    internal async Task CompleteAsync()
    {
        if (restored) return;
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        finally { Restore(); }
        output.End();
        error.End();
        await Task.WhenAll(output.Completion, error.Completion).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Restore()
    {
        output.Restore();
        error.Restore();
        Console.SetOut(originalOutput);
        Console.SetError(originalError);
        restored = true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await CompleteAsync(); }
        finally { output.Dispose(); error.Dispose(); }
    }

    private sealed class CapturedStream : IDisposable
    {
        private readonly AnonymousPipeServerStream pipe = new(PipeDirection.In, HandleInheritability.Inheritable);
        private readonly AnonymousPipeClientStream writer;
        private readonly string marker = "\0wf-log-end-" + Guid.NewGuid().ToString("N") + "\0";
        private readonly int descriptor;
        private readonly nint original;
        private bool restored;
        internal Task Completion { get; }

        internal CapturedStream(int descriptor, string stream, AttemptSpool spool, SecretRedactor redactor)
        {
            this.descriptor = descriptor;
            writer = new(PipeDirection.Out, pipe.ClientSafePipeHandle);
            try
            {
                var target = pipe.ClientSafePipeHandle.DangerousGetHandle();
                original = OperatingSystem.IsWindows() ? GetStdHandle(unchecked((uint)(-10 - descriptor))) : Dup(descriptor);
                if (OperatingSystem.IsWindows()
                    ? original == new nint(-1) || original == 0 || !SetStdHandle(unchecked((uint)(-10 - descriptor)), target)
                    : original < 0 || Dup2(target.ToInt32(), descriptor) < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Redirect task-host output into its durable capture failed.");
                Completion = DrainAsync();
            }
            catch { writer.Dispose(); pipe.Dispose(); throw; }

            async Task DrainAsync()
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
                var masking = redactor.CreateStream();
                var pending = "";
                var line = new StringBuilder();
                var fragmented = false;
                var buffer = new char[4096];
                while (true)
                {
                    var read = await reader.ReadAsync(buffer);
                    if (read == 0) throw new EndOfStreamException("Task-host output capture ended before its completion barrier.");
                    pending += new string(buffer, 0, read);
                    var end = pending.IndexOf(marker, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        Consume(masking.Append(pending[..end]));
                        Consume(masking.Complete());
                        if (line.Length > 0) spool.Log(stream, line.ToString());
                        return;
                    }
                    var keep = Math.Min(pending.Length, marker.Length - 1);
                    while (keep > 0 && !pending.AsSpan(pending.Length - keep).SequenceEqual(marker.AsSpan(0, keep))) keep--;
                    var take = pending.Length - keep;
                    if (take > 0)
                    {
                        Consume(masking.Append(pending[..take]));
                        pending = pending[take..];
                    }
                }

                void Consume(string text)
                {
                    foreach (var character in text)
                    {
                        if (character == '\n')
                        {
                            var value = line.ToString();
                            if (value.EndsWith('\r')) value = value[..^1];
                            if (value.Length > 0 || !fragmented) spool.Log(stream, value);
                            line.Clear();
                            fragmented = false;
                        }
                        else line.Append(character);
                        if (line.Length >= 16 * 1024)
                        {
                            var count = line.Length;
                            if (char.IsHighSurrogate(line[^1]) || line[^1] == '\r') count--;
                            if (count >= 16 * 1024)
                            {
                                spool.Log(stream, line.ToString(0, count));
                                line.Remove(0, count);
                                fragmented = true;
                            }
                        }
                    }
                }
            }
        }

        internal void Restore()
        {
            if (restored) return;
            if (OperatingSystem.IsWindows()
                ? !SetStdHandle(unchecked((uint)(-10 - descriptor)), original)
                : Dup2(original.ToInt32(), descriptor) < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Restore task-host standard output failed.");
            if (!OperatingSystem.IsWindows()) _ = Close(original.ToInt32());
            restored = true;
        }

        internal void End() { writer.Write(Encoding.UTF8.GetBytes(marker)); writer.Flush(); }
        public void Dispose() { writer.Dispose(); pipe.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(uint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(uint handle, nint value);
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int descriptor);
    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Dup2(int source, int destination);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
