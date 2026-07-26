using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for the codec used by the registry-stored hook commands.
/// </summary>
public sealed class Base64TextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("notepad.exe")]
    [InlineData("\"C:\\Program Files\\App\\run.exe\" --flag \"a b\"")]
    [InlineData("robocopy \u00e9\u00fc\u4e2d\u6587.txt")]
    public void RoundTripsValues(string value) =>
        Assert.Equal(value, Base64Text.Decode(Base64Text.Encode(value)));

    [Fact]
    public void EncodesNullAsEmpty() => Assert.Equal(string.Empty, Base64Text.Decode(Base64Text.Encode(null)));

    [Fact]
    public void EncodedValueIsBase64() =>
        Assert.Equal("bm90ZXBhZC5leGU=", Base64Text.Encode("notepad.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    public void MalformedInputDecodesToEmpty(string? encoded) =>
        Assert.Equal(string.Empty, Base64Text.Decode(encoded));
}
