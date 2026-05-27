using DriverUpdater.Core.Results;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Results;

public class ResultErrorTests
{
    [Fact]
    public void From_code_and_message_creates_error_with_no_cause()
    {
        var error = ResultError.From("E001", "Boom");

        error.Code.Should().Be("E001");
        error.Message.Should().Be("Boom");
        error.Cause.Should().BeNull();
    }

    [Fact]
    public void From_code_and_exception_uses_exception_message_and_captures_cause()
    {
        var exception = new InvalidOperationException("kaboom");

        var error = ResultError.From("E002", exception);

        error.Code.Should().Be("E002");
        error.Message.Should().Be("kaboom");
        error.Cause.Should().BeSameAs(exception);
    }

    [Fact]
    public void ToString_returns_code_colon_message()
    {
        var error = ResultError.From("E003", "Disk full");

        error.ToString().Should().Be("E003: Disk full");
    }
}
