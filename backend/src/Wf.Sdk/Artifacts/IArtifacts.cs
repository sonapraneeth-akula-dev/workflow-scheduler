namespace Wf.Sdk;

public interface IArtifacts
{
    Task<Uri> PublishAsync(string name, Stream content, string contentType, CancellationToken cancellationToken);
}
