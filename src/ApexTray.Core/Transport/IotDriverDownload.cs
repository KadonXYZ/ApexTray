using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ApexTray.Core.Transport;

// Fetches iot_driver from the RongYuan server the official app updates itself from, so ApexTray
// works without the AJAZZ app installed and without redistributing the vendor's binary.
public static partial class IotDriverDownload
{
    private const string Server = "https://api2.rongyuan.tech:3816";
    // Registration number of Shenzhen RongYuan Technology, the signer of every official iot_driver.
    // It survives certificate renewals, unlike the thumbprint.
    private const string SignerId = "SERIALNUMBER=91440300MA5FNF428F";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexTray", "iot_driver");

    public static string ExePath => Path.Combine(Folder, "iot_driver.exe");

    public static async Task DownloadAsync(CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync($"{Server}/api/v2/get_iot_version", new { sys_type = "win" }, ct);
        response.EnsureSuccessStatusCode();
        var filePath = ParseFilePath(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidDataException("The AJAZZ server did not offer an iot_driver for Windows.");

        var packed = await Http.GetByteArrayAsync($"{Server}/download/{filePath}", ct);
        Directory.CreateDirectory(Folder);
        var partial = ExePath + ".download";
        try
        {
            await using (var output = File.Create(partial))
            await using (var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
                await inflate.CopyToAsync(output, ct);

            if (!IsSignedByRongYuan(partial))
                throw new InvalidDataException("The downloaded iot_driver is not signed by Shenzhen RongYuan, so it was discarded.");
            File.Move(partial, ExePath, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    // Response shape: {"code":0,"data":{"iot_driver_version_str":"v218","file_path":"iot_upgrade_file/win/iot_v218",...}}.
    internal static string? ParseFilePath(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0) return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        var path = data.TryGetProperty("file_path", out var f) ? f.GetString() : null;
        // The path is appended to the download URL, so anything that could leave /download is refused.
        return path is { Length: > 0 } && !path.Contains("..") && !path.StartsWith('/') && Uri.IsWellFormedUriString(path, UriKind.Relative)
            ? path
            : null;
    }

    public static bool IsSignedByRongYuan(string file)
    {
        if (!WinVerifyTrustFile(file)) return false;
#pragma warning disable SYSLIB0057 // The replacement loaders read certificate files, not the signer of a signed PE.
        using var signer = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
        return signer.Subject.Contains(SignerId, StringComparison.Ordinal);
    }

    private static bool WinVerifyTrustFile(string file)
    {
        var path = Marshal.StringToHGlobalUni(file);
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path }, fileInfo, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                UnionChoice = WtdChoiceFile,
                File = fileInfo,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(-1, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(path);
        }
    }

    private const uint WtdUiNone = 2, WtdChoiceFile = 1;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);
}
