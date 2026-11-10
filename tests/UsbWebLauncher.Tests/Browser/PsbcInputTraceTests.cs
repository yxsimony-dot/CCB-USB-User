using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PsbcInputTraceTests
{
    private static readonly Uri Login = new("https://corpebank.psbc.com/#/login");

    [Fact]
    public void NeverEvaluatesScriptsBeforeSuccessfulBankDocumentLoad()
    {
        var navigation = new PsbcInputTraceNavigation();
        Assert.False(navigation.CanObserve(Login));
        navigation.Starting(2);
        Assert.False(navigation.CanObserve(Login));
        navigation.Completed(2, false, 412, Login);
        Assert.False(navigation.CanObserve(Login));
        navigation.Starting(4);
        Assert.False(navigation.CanObserve(Login));
        navigation.Completed(4, true, 200, Login);
        Assert.True(navigation.CanObserve(Login));
    }

    [Fact]
    public void NewNavigationImmediatelyDisablesScriptsAndIgnoresLateCompletions()
    {
        var navigation = new PsbcInputTraceNavigation();
        navigation.Starting(2);
        navigation.Completed(2, true, 200, Login);
        Assert.True(navigation.CanObserve(Login));
        navigation.Starting(4);
        navigation.Completed(2, true, 200, Login);
        Assert.False(navigation.CanObserve(Login));
        navigation.Completed(4, false, 412, Login);
        Assert.False(navigation.CanObserve(Login));
    }

    [Fact]
    public void OnlyObservesLoginRouteOnSuccessfullyLoadedBankDocument()
    {
        var navigation = new PsbcInputTraceNavigation();
        navigation.Starting(2);
        navigation.Completed(2, true, 200, new Uri("https://corpebank.psbc.com/"));
        Assert.False(navigation.CanObserve(new Uri("https://corpebank.psbc.com/")));
        Assert.True(navigation.CanObserve(Login));
        Assert.False(navigation.CanObserve(new Uri("https://corpebank.psbc.com/#/index")));
        Assert.False(navigation.CanObserve(new Uri("https://example.com/#/login")));
    }

    [Theory]
    [InlineData(false, 200, "https://corpebank.psbc.com/#/login")]
    [InlineData(true, 412, "https://corpebank.psbc.com/#/login")]
    [InlineData(true, 0, "https://corpebank.psbc.com/#/login")]
    [InlineData(true, 200, "https://example.com/#/login")]
    public void FailedOrForeignDocumentNeverEnablesObservation(bool success, int status, string source)
    {
        var navigation = new PsbcInputTraceNavigation();
        navigation.Starting(2);
        navigation.Completed(2, success, status, new Uri(source));
        Assert.False(navigation.CanObserve(Login));
    }

    private static Dictionary<string, object> Sample() => new()
    {
        ["eventType"] = "keydown", ["elapsedMs"] = 20, ["maskLength"] = 0,
        ["documentFocused"] = true, ["fieldFocused"] = true, ["trusted"] = true,
        ["connected"] = 1, ["stopped"] = 0, ["busy"] = 1
    };

    private static string Json(Dictionary<string, object> sample) =>
        JsonSerializer.Serialize(new[] { sample });

    [Fact]
    public void AcceptsOnlyContentFreeTypedSamples()
    {
        var sample = Assert.Single(PsbcInputTrace.Parse(Json(Sample())));
        Assert.Equal("keydown", sample.EventType);
        Assert.Equal(0, sample.MaskLength);
        Assert.True(sample.FieldFocused);
    }

    [Theory]
    [InlineData("eventType", "SYNTHETIC_SECRET")]
    [InlineData("elapsedMs", -1)]
    [InlineData("elapsedMs", 600001)]
    [InlineData("maskLength", 65)]
    [InlineData("maskLength", -2)]
    [InlineData("maskLength", "SYNTHETIC_SECRET")]
    [InlineData("documentFocused", "SYNTHETIC_SECRET")]
    [InlineData("connected", 2)]
    [InlineData("busy", "SYNTHETIC_SECRET")]
    [InlineData("extraSecret", "SYNTHETIC_SECRET")]
    public void RejectsUnexpectedPropertiesTypesOrRanges(string key, object value)
    {
        var sample = Sample();
        sample[key] = value;
        Assert.Empty(PsbcInputTrace.Parse(Json(sample)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("not json")]
    [InlineData("[{}]")]
    public void MalformedDataIsIgnored(string json) => Assert.Empty(PsbcInputTrace.Parse(json));

    [Fact]
    public void BoundsInputAndBatchSize()
    {
        Assert.Empty(PsbcInputTrace.Parse(new string(' ', 65537)));
        Assert.Empty(PsbcInputTrace.Parse(JsonSerializer.Serialize(Enumerable.Repeat(Sample(), 129))));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("automatic", false)]
    [InlineData("manual", true)]
    public void ObservationRequiresExplicitManualMode(string? value, bool expected) =>
        Assert.Equal(expected, PsbcInputTrace.IsManualMode(value));
}
