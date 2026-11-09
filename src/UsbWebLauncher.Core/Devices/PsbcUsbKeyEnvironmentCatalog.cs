namespace UsbWebLauncher.Core.Devices;

public static class PsbcUsbKeyEnvironmentCatalog
{
    private static readonly IReadOnlyDictionary<UsbKeyVendorFamily, string[]> ProviderNames =
        new Dictionary<UsbKeyVendorFamily, string[]>
        {
            [UsbKeyVendorFamily.FeitianEnterSafe] =
                ["InterPass3000CSPCompany for PSBC V1.0"],
            [UsbKeyVendorFamily.Hdzb] =
                ["HDZB CSP for PSBC", "HDZB PSBC CSP"],
            [UsbKeyVendorFamily.Tendyron] =
                ["Tendyron OnKey193 CSP PSBC v1.0"],
            [UsbKeyVendorFamily.Excelsecu] =
                ["Excelsecu eBank CSP for PSBC"],
            [UsbKeyVendorFamily.WatchData] =
                ["Watchdata PSBC CSP"]
        };

    public static IReadOnlyList<string> GetProviderNames(UsbKeyVendorFamily vendorFamily) =>
        ProviderNames[vendorFamily];
}
