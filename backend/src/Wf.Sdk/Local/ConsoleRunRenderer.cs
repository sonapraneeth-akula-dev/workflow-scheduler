using Wf.Contracts;
using Wf.Contracts.Events;

namespace Wf.Sdk.Local;

public sealed class ConsoleRunRenderer(TextWriter output)
{
    private readonly TextWriter output = TextWriter.Synchronized(output ?? throw new ArgumentNullException(nameof(output)));

    public void Render(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var state = @event.Entity == EventEntity.Run
            ? ((RunStatus)@event.State).ToString() : ((ExecutionStatus)@event.State).ToString();
        output.WriteLine($"{@event.Entity}{(@event.Key is { } key ? $" {key}" : "")}: {state}");
    }

    public void Log(string message) => output.WriteLine(message);
}
