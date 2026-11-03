using MangaIngestWithUpscaling.Shared.Services.Upscaling;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.Upscaling;

public class ModelArchitecturePreferenceTests
{
    [Fact]
    public void Resolve_Unset_IsTheBuiltInOrder()
    {
        ModelArchitecture[] builtIn =
        [
            ModelArchitecture.FdatM,
            ModelArchitecture.Esrgan,
            ModelArchitecture.Span,
            ModelArchitecture.FdatXl,
            ModelArchitecture.Dat2,
            ModelArchitecture.HatL,
        ];

        Assert.Equal(builtIn, ModelArchitecturePreference.Default);
        Assert.Equal(builtIn, ModelArchitecturePreference.Resolve(null));
        Assert.Equal(builtIn, ModelArchitecturePreference.Resolve("   "));
    }

    [Fact]
    public void Resolve_NamedArchitecturesFirst_AndTheRestKeepTheirPlace()
    {
        // The point of the setting: FDAT_XL sits fourth by default, so it is only reached when
        // FDAT_M, ESRGAN and SPAN are all missing. Naming it has to move it to the front without
        // dropping anything else.
        Assert.Equal(
            [
                ModelArchitecture.FdatXl,
                ModelArchitecture.FdatM,
                ModelArchitecture.Esrgan,
                ModelArchitecture.Span,
                ModelArchitecture.Dat2,
                ModelArchitecture.HatL,
            ],
            ModelArchitecturePreference.Resolve("FDAT_XL")
        );
    }

    [Fact]
    public void Resolve_AcceptsCaseSeparatorsAndEnumSpellings()
    {
        Assert.Equal(
            [
                ModelArchitecture.HatL,
                ModelArchitecture.FdatXl,
                ModelArchitecture.FdatM,
                ModelArchitecture.Esrgan,
                ModelArchitecture.Span,
                ModelArchitecture.Dat2,
            ],
            ModelArchitecturePreference.Resolve("hat_l; fdatxl , FdatM")
        );
    }

    [Fact]
    public void Resolve_SkipsUnknownTokensAndRepeats()
    {
        // Skipped rather than thrown: the configuration validator rejects an unknown name at startup,
        // so this only sees one when the config was built in code.
        Assert.Equal(
            [
                ModelArchitecture.Span,
                ModelArchitecture.FdatM,
                ModelArchitecture.Esrgan,
                ModelArchitecture.FdatXl,
                ModelArchitecture.Dat2,
                ModelArchitecture.HatL,
            ],
            ModelArchitecturePreference.Resolve("BOGUS,SPAN,span,FDAT_M")
        );
    }

    [Theory]
    [InlineData("ESRGAN", ModelArchitecture.Esrgan)]
    [InlineData("esrgan", ModelArchitecture.Esrgan)]
    [InlineData(" SPAN ", ModelArchitecture.Span)]
    [InlineData("FDAT_M", ModelArchitecture.FdatM)]
    [InlineData("FdatM", ModelArchitecture.FdatM)]
    [InlineData("FDAT_XL", ModelArchitecture.FdatXl)]
    [InlineData("fdatxl", ModelArchitecture.FdatXl)]
    [InlineData("DAT2", ModelArchitecture.Dat2)]
    [InlineData("HAT_L", ModelArchitecture.HatL)]
    [InlineData("HATL", ModelArchitecture.HatL)]
    public void Parse_AcceptsTheDocumentedSpellings(string token, ModelArchitecture expected) =>
        Assert.Equal(expected, ModelArchitecturePreference.Parse(token));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("FDAT")]
    [InlineData("FDAT_M_XL")]
    [InlineData("Unknown")]
    [InlineData("ESRGAN2")]
    public void Parse_RejectsAnythingElse(string token) =>
        Assert.Null(ModelArchitecturePreference.Parse(token));
}
