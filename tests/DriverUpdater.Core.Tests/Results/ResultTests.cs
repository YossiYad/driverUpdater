using DriverUpdater.Core.Results;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Success_carries_value_and_reports_success()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_carries_error_and_reports_failure()
    {
        var error = ResultError.From("E001", "Boom");
        var result = Result<int>.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Accessing_value_on_failure_throws()
    {
        var result = Result<int>.Failure("E", "msg");

        var act = () => _ = result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Accessing_error_on_success_throws()
    {
        var result = Result<int>.Success(1);

        var act = () => _ = result.Error;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryGetValue_returns_true_and_yields_value_on_success()
    {
        var result = Result<string>.Success("hello");

        var ok = result.TryGetValue(out var value);

        ok.Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public void TryGetValue_returns_false_on_failure()
    {
        var result = Result<string>.Failure("E", "no");

        var ok = result.TryGetValue(out var value);

        ok.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void Map_projects_value_on_success()
    {
        var result = Result<int>.Success(3).Map(x => x * 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(6);
    }

    [Fact]
    public void Map_propagates_error_on_failure()
    {
        var error = ResultError.From("E", "fail");
        var result = Result<int>.Failure(error).Map(x => x * 2);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Bind_chains_results_on_success()
    {
        var result = Result<int>.Success(5)
            .Bind(x => Result<string>.Success($"#{x}"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("#5");
    }

    [Fact]
    public void Bind_short_circuits_on_initial_failure()
    {
        var bound = 0;
        var error = ResultError.From("E", "x");

        var result = Result<int>.Failure(error)
            .Bind(x => { bound = x; return Result<string>.Success("ignored"); });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
        bound.Should().Be(0);
    }

    [Fact]
    public void Match_calls_success_branch_for_success()
    {
        var result = Result<int>.Success(7);

        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");

        output.Should().Be("ok:7");
    }

    [Fact]
    public void Match_calls_failure_branch_for_failure()
    {
        var result = Result<int>.Failure("E42", "oops");

        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");

        output.Should().Be("err:E42");
    }

    [Fact]
    public void Implicit_conversion_from_value_creates_success()
    {
        Result<int> result = 99;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(99);
    }

    [Fact]
    public void Implicit_conversion_from_error_creates_failure()
    {
        Result<int> result = ResultError.From("E", "msg");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("E");
    }
}
