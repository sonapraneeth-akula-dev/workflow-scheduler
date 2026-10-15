using Wf.Validation.Rules;

namespace Wf.Validation;

public sealed record ValidationOptions(int TypeQuota = TeamRegistryRules.DefaultTypeQuota);
