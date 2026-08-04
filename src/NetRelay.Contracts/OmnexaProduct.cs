namespace NetRelay.Contracts;

public static class OmnexaProduct
{
    public const string BaseAddress = "https://omnexa.lansil.cn/";
    public const string ApiId = "app_YPDP33LMk4gw_Q3_s1kBqvAR";
    public const string ApplicationId = "netrelay";
    public const string EnvironmentId = "production";
    public const string Channel = "stable";
    public const string OperatingSystem = "windows";
    public const string Architecture = "x64";

    // Public trust anchor copied from the NetRelay production integration page.
    public const string RootPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEg5CG0HquHLzZP-MVxDxoMnlkV5Ss7FdAxWomvZ9b6IMxsN9iasvX51aDOhN_tMBFcZs4b20Rn_XbAbCL8TLDVA";

    // Public domain-separation salt. It must remain stable for this product.
    public const string FingerprintProtocolSalt =
        "netrelay:omnexa-native-v1:app_YPDP33LMk4gw_Q3_s1kBqvAR";
}
