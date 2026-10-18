using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Engine.Core.Tests;

public sealed class FailureClassifierTests
{
    [Theory]
    [InlineData(FailureCategory.TaskError)]
    [InlineData(FailureCategory.Timeout)]
    [InlineData(FailureCategory.Aborted)]
    [InlineData(FailureCategory.AgentLost)]
    [InlineData(FailureCategory.ResourceLimit)]
    [InlineData(FailureCategory.ValidationError)]
    [InlineData(FailureCategory.Placement)]
    [InlineData(FailureCategory.PlatformError)]
    public void ScriptedTypedFailurePreservesEveryCategory(FailureCategory category)
    {
        Action script = () => throw new TaskFailureException("Explicit failure fact, not a message heuristic.", category: category);
        var error = Assert.Throws<TaskFailureException>(script);
        Assert.Equal(category, FailureClassifier.Classify(error));
        Assert.Equal(category, FailureClassifier.Classify(error));
    }

    [Theory]
    [InlineData(FailureCategory.TaskError)]
    [InlineData(FailureCategory.ValidationError)]
    [InlineData(FailureCategory.ResourceLimit)]
    [InlineData(FailureCategory.Placement)]
    [InlineData(FailureCategory.AgentLost)]
    [InlineData(FailureCategory.PlatformError)]
    public void AuthorFailureMetadataPrecedesConcurrentCancellation(FailureCategory category)
    {
        var error = new TaskFailureException("author failure", category: category,
            innerException: new OperationCanceledException());
        Assert.Equal(category, FailureClassifier.Classify(error, timedOut: true, aborted: true));
    }

    [Fact]
    public void NonRetryableMetadataIsNotChanged()
    {
        var error = new NonRetryableException("permanent", new TimeoutException());
        Assert.Equal(FailureCategory.TaskError, FailureClassifier.Classify(error, timedOut: true, aborted: true));
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(false, false, FailureCategory.PlatformError)]
    [InlineData(false, true, FailureCategory.Aborted)]
    [InlineData(true, false, FailureCategory.Timeout)]
    [InlineData(true, true, FailureCategory.Timeout)]
    public void CancellationNeedsAnActualCauseAndTimeoutPrecedesAbort(bool timedOut, bool aborted, FailureCategory expected)
    {
        Assert.Equal(expected, FailureClassifier.Classify(new OperationCanceledException(), timedOut: timedOut, aborted: aborted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnattributedCancellationKeepsItsFailureDomain(bool taskExecution)
    {
        var domain = taskExecution ? FailureCategory.TaskError : FailureCategory.PlatformError;
        Assert.Equal(domain, FailureClassifier.Classify(new OperationCanceledException("Timeout Aborted"), domain));
    }

    [Theory]
    [InlineData(FailureCategory.TaskError)]
    [InlineData(FailureCategory.PlatformError)]
    public void NormalErrorsAreNotReclassifiedByCancellationOrMessage(FailureCategory domain)
    {
        foreach (var message in Enum.GetNames<FailureCategory>())
            Assert.Equal(domain, FailureClassifier.Classify(new InvalidOperationException(message), domain,
                timedOut: true, aborted: true));
        Assert.Equal(domain, FailureClassifier.Classify(new ArgumentException("validation"), domain));
        Assert.Equal(domain, FailureClassifier.Classify(new OutOfMemoryException("resource"), domain));
    }

    [Fact]
    public void UnknownErrorDefaultsToPlatformAndDoesNotInspectInnerMessages()
    {
        Assert.Equal(FailureCategory.PlatformError, FailureClassifier.Classify(new IOException("unknown")));
        Assert.Equal(FailureCategory.PlatformError, FailureClassifier.Classify(
            new InvalidOperationException("outer", new TaskFailureException("inner", category: FailureCategory.Placement))));
        Assert.Equal(FailureCategory.PlatformError, FailureClassifier.Classify(
            new AggregateException(new TimeoutException(), new OperationCanceledException())));
    }

    [Fact]
    public void TypedTimeoutPrecedesAbort()
    {
        Assert.Equal(FailureCategory.Timeout, FailureClassifier.Classify(new TimeoutException(), aborted: true));
    }

    [Theory]
    [InlineData(false, true, FailureCategory.Aborted)]
    [InlineData(true, false, FailureCategory.Timeout)]
    [InlineData(true, true, FailureCategory.Timeout)]
    public void ExplicitCancellationCanCompleteWithoutAnException(bool timedOut, bool aborted, FailureCategory expected)
    {
        Assert.Equal(expected, FailureClassifier.Classify(null, timedOut: timedOut, aborted: aborted));
    }

    [Fact]
    public void SuccessAndInvalidInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => FailureClassifier.Classify(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailureClassifier.Classify(new Exception(), (FailureCategory)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailureClassifier.Classify(
            new TaskFailureException("invalid", category: (FailureCategory)(-1)), timedOut: true, aborted: true));
        foreach (var category in Enum.GetValues<FailureCategory>().Except([FailureCategory.TaskError, FailureCategory.PlatformError]))
            Assert.Throws<ArgumentOutOfRangeException>(() => FailureClassifier.Classify(new Exception(), category));
    }

    [Theory]
    [InlineData(FailureCategory.TaskError, 0)]
    [InlineData(FailureCategory.Timeout, 1)]
    [InlineData(FailureCategory.AgentLost, 2)]
    [InlineData(FailureCategory.PlatformError, 3)]
    [InlineData(FailureCategory.ValidationError, 4)]
    [InlineData(FailureCategory.Aborted, 5)]
    [InlineData(FailureCategory.ResourceLimit, 6)]
    [InlineData(FailureCategory.Placement, 7)]
    public void ExistingNumericAndStringSerializationRemainsStable(FailureCategory category, int value)
    {
        Assert.Equal(value, (int)category);
        var numeric = JsonSerializer.Serialize(category);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), numeric);
        Assert.Equal(category, JsonSerializer.Deserialize<FailureCategory>(numeric));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter<FailureCategory>() } };
        var text = JsonSerializer.Serialize(category, options);
        Assert.Equal($"\"{category}\"", text);
        Assert.Equal(category, JsonSerializer.Deserialize<FailureCategory>(text, options));
        Assert.Equal(8, Enum.GetValues<FailureCategory>().Length);
    }
}
