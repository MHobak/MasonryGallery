namespace MasonryGallery.UI.Tests;

using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

/// <summary>
/// The layering of design §4: Domain ← Application ← Infrastructure / UI ← Host. Project references enforce most of
/// it at compile time; these rules catch a reference added later, or UI code reaching past Application into storage.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(MasonryGallery.Domain.MediaItem).Assembly;
    private static readonly Assembly Application = typeof(MasonryGallery.Application.Ports.IMediaRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(MasonryGallery.Infrastructure.Persistence.SqliteMediaRepository).Assembly;
    private static readonly Assembly Ui = typeof(GalleryService).Assembly;

    private static void ShouldNotDependOn(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
        result.IsSuccessful.Should().BeTrue(
            $"{assembly.GetName().Name} must not use {string.Join(", ", forbidden)}; offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_depends_on_nothing_else()
        => ShouldNotDependOn(Domain, "MasonryGallery.Application", "MasonryGallery.Infrastructure", "MasonryGallery.UI",
            "MasonryGallery.Host", "Microsoft.Extensions", "Microsoft.Data", "System.Reactive");

    [Fact]
    public void Application_knows_ports_not_adapters()
        => ShouldNotDependOn(Application, "MasonryGallery.Infrastructure", "MasonryGallery.UI", "MasonryGallery.Host",
            "Microsoft.Data.Sqlite", "Dapper", "SkiaSharp", "Microsoft.AspNetCore", "Photino");

    [Fact]
    public void Infrastructure_has_no_ui()
        => ShouldNotDependOn(Infrastructure, "MasonryGallery.UI", "MasonryGallery.Host", "Microsoft.AspNetCore", "Photino");

    [Fact]
    public void Ui_goes_through_application_not_storage()
        => ShouldNotDependOn(Ui, "MasonryGallery.Infrastructure", "MasonryGallery.Host", "Microsoft.Data.Sqlite", "Dapper",
            "SkiaSharp", "Photino");
}
