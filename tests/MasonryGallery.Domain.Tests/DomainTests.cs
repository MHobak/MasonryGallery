using FluentAssertions;
using MasonryGallery.Domain;
using Xunit;

namespace MasonryGallery.Domain.Tests;

public class DomainTests
{
    [Fact]
    public void Result_success_and_failure()
    {
        Result<int>.Success(5).Value.Should().Be(5);
        Result<int>.Failure("x").IsSuccess.Should().BeFalse();
        Result.Success().IsSuccess.Should().BeTrue();
        Result.Failure("boom").Error.Should().Be("boom");
    }

    [Fact]
    public void Result_implicit_conversion_wraps_value()
    {
        Result<string> r = "hi";
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be("hi");
    }

    [Theory]
    [InlineData(LibraryStatus.Online, true, false)]
    [InlineData(LibraryStatus.Scanning, false, true)]
    [InlineData(LibraryStatus.Offline, false, false)]
    public void Library_status_flags(LibraryStatus s, bool online, bool scanning)
    {
        var l = new Library("n", "/p") { Status = s };
        l.IsOnline.Should().Be(online);
        l.IsScanning.Should().Be(scanning);
    }
}
