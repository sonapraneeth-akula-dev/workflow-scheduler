using System.Text;

namespace Wf.ControlPlane.Auth;

public static class WorkflowIdentity
{
    public static bool TryGetTypeScope(string? workflowId, out string scope)
    {
        scope = "";
        if (workflowId is null) return false;
        var segments = workflowId.Split('/');
        if (segments.Length != 3 || segments.Any(part => string.IsNullOrWhiteSpace(part) ||
            part.EnumerateRunes().Take(129).Count() > 128 || part is "." or ".." ||
            part.Any(character => char.IsControl(character) || character == '\\')))
            return false;
        scope = segments[0] + "/" + segments[1];
        return true;
    }
}
