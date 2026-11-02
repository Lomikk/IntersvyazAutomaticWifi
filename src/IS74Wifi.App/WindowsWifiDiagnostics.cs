using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace IS74Wifi.App;

/// <summary>Native Wi-Fi observations for the foreground diagnostic screen/report only.</summary>
internal static class WindowsWifiDiagnostics
{
    internal const int InterfaceSize = 532;
    internal const int ConnectionSize = 604;
    internal const int BssEntrySize = 360;

    internal static Task<WifiDiagnosticSnapshot> CaptureAsync(bool scan, CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureCoreAsync(scan, cancellationToken), cancellationToken);

    internal static WifiDiagnosticSnapshot ReadCached() => CaptureCoreAsync(false, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<WifiDiagnosticSnapshot> CaptureCoreAsync(bool scan, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return WifiDiagnosticSnapshot.Unavailable("Native Wi-Fi доступен только в Windows.");
        try
        {
            var error = WlanOpenHandle(2, 0, out _, out var handle);
            if (error != 0) return WifiDiagnosticSnapshot.Unavailable(DescribeError("Служба Wi-Fi", error));
            try
            {
                var interfaces = ReadInterfaces(handle);
                var scanErrors = scan ? await ScanOnceAsync(handle, interfaces, cancellationToken).ConfigureAwait(false) : [];
                var adapters = new List<WifiAdapterSnapshot>();
                foreach (var item in interfaces)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var errors = new List<string>();
                    if (scanErrors.TryGetValue(item.Id, out var scanError)) errors.Add(scanError);
                    bool? radioOn = null;
                    var radio = Query(handle, item.Id, 4, out var radioError);
                    if (radio is not null) radioOn = ParseRadio(radio);
                    else errors.Add(DescribeError("Состояние радио", radioError));

                    WifiConnection? connection = null;
                    if (item.State is 1 or 2)
                    {
                        var bytes = Query(handle, item.Id, 7, out var connectionError);
                        if (bytes is not null)
                        {
                            try { connection = ParseConnection(bytes); }
                            catch (InvalidDataException) { errors.Add("Текущее подключение: Windows вернула неполные данные."); }
                        }
                        else errors.Add(DescribeError("Текущее подключение", connectionError));
                    }
                    var points = ReadPoints(handle, item.Id, errors);
                    if (connection is not null)
                    {
                        var current = points.FirstOrDefault(ap => ap.Bssid == connection.Bssid && ap.Ssid == connection.Ssid);
                        int? channel = current is null ? null : WifiDiagnosticText.Channel(current.FrequencyKhz);
                        if (channel is null)
                        {
                            var channelBytes = Query(handle, item.Id, 8, out _);
                            if (channelBytes is { Length: >= 4 })
                            {
                                var number = U32(channelBytes, 0);
                                if (number is > 0 and <= 233) channel = (int)number;
                            }
                        }
                        connection = connection with { Channel = channel, FrequencyKhz = current?.FrequencyKhz ?? 0, RssiDbm = current?.RssiDbm };
                    }
                    adapters.Add(new(item.Id, item.Name, item.State, radioOn, connection, points, errors.ToArray()));
                }
                return new(DateTimeOffset.UtcNow, adapters.ToArray());
            }
            finally { WlanCloseHandle(handle, 0); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex)
        {
            return WifiDiagnosticSnapshot.Unavailable(ex.Message);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NetworkInformationException)
        {
            return WifiDiagnosticSnapshot.Unavailable("Не удалось получить данные Wi-Fi: " + ex.GetType().Name);
        }
    }

    private static List<NativeInterface> ReadInterfaces(nint handle)
    {
        var error = WlanEnumInterfaces(handle, 0, out var list);
        if (error != 0) throw new InvalidDataException(DescribeError("Список адаптеров", error));
        try
        {
            var count = (uint)Marshal.ReadInt32(list);
            if (count > 256) throw new InvalidDataException("Invalid WLAN interface count.");
            var result = new List<NativeInterface>();
            var names = new Dictionary<Guid, string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    if (Guid.TryParse(adapter.Id, out var id)) names[id] = adapter.Name;
            }
            catch (NetworkInformationException) { }
            for (var i = 0; i < count; i++)
            {
                var bytes = Copy(list + 8 + i * InterfaceSize, InterfaceSize);
                var id = new Guid(bytes.AsSpan(0, 16));
                var description = Encoding.Unicode.GetString(bytes, 16, 512).Split('\0')[0];
                result.Add(new(id, names.GetValueOrDefault(id, description), U32(bytes, 528)));
            }
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    private static byte[]? Query(nint handle, Guid id, uint opcode, out uint error)
    {
        error = WlanQueryInterface(handle, ref id, opcode, 0, out var size, out var data, out _);
        if (error != 0) return null;
        try
        {
            if (size > 1024 * 1024) throw new InvalidDataException("Invalid WLAN query size.");
            return Copy(data, (int)size);
        }
        finally { WlanFreeMemory(data); }
    }

    private static WifiAccessPoint[] ReadPoints(nint handle, Guid id, List<string> errors)
    {
        var error = WlanGetNetworkBssList(handle, ref id, 0, 3, false, 0, out var list);
        if (error != 0)
        {
            errors.Add(DescribeError("Список точек доступа", error));
            return [];
        }
        try
        {
            var size = (uint)Marshal.ReadInt32(list);
            if (size is < 8 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid WLAN BSS list size.");
            return ParseBssList(Copy(list, (int)size));
        }
        catch (InvalidDataException)
        {
            errors.Add("Список точек доступа: Windows вернула неполные данные.");
            return [];
        }
        finally { WlanFreeMemory(list); }
    }

    // Fixed SDK layouts (wlanapi.h), not localized netsh text. Ignore untrusted IE blobs.
    internal static WifiAccessPoint[] ParseBssList(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) throw new InvalidDataException("Truncated WLAN BSS header.");
        var total = U32(bytes, 0);
        var count = U32(bytes, 4);
        if (total < 8 || total > bytes.Length || count > (total - 8) / BssEntrySize)
            throw new InvalidDataException("Truncated WLAN BSS entries.");
        var result = new List<WifiAccessPoint>();
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.Slice(8 + i * BssEntrySize, BssEntrySize);
            var rssi = BinaryPrimitives.ReadInt32LittleEndian(entry[56..]);
            result.Add(new(ReadSsid(entry), Mac(entry.Slice(40, 6)), U32(entry, 52),
                Quality(U32(entry, 60)), rssi is >= -127 and <= 0 ? rssi : null, U32(entry, 92)));
        }
        return result.DistinctBy(ap => (ap.Ssid, ap.Bssid, ap.FrequencyKhz)).ToArray();
    }

    internal static WifiConnection ParseConnection(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ConnectionSize) throw new InvalidDataException("Truncated WLAN connection.");
        return new(ReadSsid(bytes[520..]), Mac(bytes.Slice(560, 6)), U32(bytes, 568),
            Quality(U32(bytes, 576)), U32(bytes, 580), U32(bytes, 584));
    }

    internal static bool? ParseRadio(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;
        var count = U32(bytes, 0);
        if (count == 0 || count > 64 || count > (bytes.Length - 4) / 12) return null;
        var allOff = true;
        for (var i = 0; i < count; i++)
        {
            var software = U32(bytes, 8 + i * 12);
            var hardware = U32(bytes, 12 + i * 12);
            if (software == 1 && hardware == 1) return true;
            if (software != 2 && hardware != 2) allOff = false;
        }
        return allOff ? false : null;
    }

    private static string ReadSsid(ReadOnlySpan<byte> bytes)
    {
        var length = U32(bytes, 0);
        if (length > 32 || bytes.Length < 4 + length) throw new InvalidDataException("Invalid WLAN SSID length.");
        return Encoding.UTF8.GetString(bytes.Slice(4, (int)length));
    }
    private static string Mac(ReadOnlySpan<byte> bytes) => string.Join(":", bytes.ToArray().Select(value => value.ToString("x2")));
    private static int? Quality(uint quality) => quality <= 100 ? (int)quality : null;
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static byte[] Copy(nint pointer, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    internal static string DescribeError(string operation, uint code) => operation + ": " + (code switch
    {
        5 => "доступ запрещён. Проверьте разрешение на местоположение в параметрах конфиденциальности Windows (код 5).",
        1062 => "служба WLAN AutoConfig не запущена (код 1062).",
        5023 => "подключение уже изменилось или отсутствует (код 5023).",
        1168 => "адаптер или подключение не найдено (код 1168).",
        50 => "драйвер не поддерживает запрос (код 50).",
        _ => $"данные недоступны (код {code})."
    });

    // One request per interface on R, bounded by 4 seconds, with no periodic scan loop.
    private static async Task<Dictionary<Guid, string>> ScanOnceAsync(nint handle,
        List<NativeInterface> interfaces, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<Guid, string>();
        var pending = interfaces.ToDictionary(item => item.Id,
            _ => new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously));
        ScanNotification callback = (pointer, _) =>
        {
            try
            {
                var data = Marshal.PtrToStructure<NotificationData>(pointer);
                if (data.Source == 8 && data.Code is 7 or 8 && pending.TryGetValue(data.Id, out var completion))
                    completion.TrySetResult(data.Code);
            }
            catch { /* Never unwind across a native callback. */ }
        };
        var registrationError = WlanRegisterNotification(handle, 8, true, callback, 0, 0, out _);
        if (registrationError != 0)
        {
            foreach (var item in interfaces) errors[item.Id] = DescribeError("Обновление списка", registrationError);
            return errors;
        }
        try
        {
            foreach (var item in interfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = item.Id;
                var error = WlanScan(handle, ref id, 0, 0, 0);
                if (error != 0)
                {
                    errors[id] = DescribeError("Сканирование", error);
                    pending[id].TrySetResult(8);
                }
            }
            try { await Task.WhenAll(pending.Values.Select(value => value.Task)).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            foreach (var item in interfaces)
            {
                var task = pending[item.Id].Task;
                if (errors.ContainsKey(item.Id)) continue;
                if (!task.IsCompleted) errors[item.Id] = "Windows не подтвердила завершение сканирования за 4 с; показан доступный кеш.";
                else if (task.Result != 7) errors[item.Id] = "Windows сообщила об ошибке сканирования; показан доступный кеш.";
            }
            return errors;
        }
        finally
        {
            // Unregister outside the callback; Windows waits for in-flight callbacks.
            WlanRegisterNotification(handle, 0, true, null, 0, 0, out _);
            GC.KeepAlive(callback);
        }
    }

    private sealed record NativeInterface(Guid Id, string Name, uint State);
    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationData { public uint Source; public uint Code; public Guid Id; public uint Size; public nint Data; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ScanNotification(nint data, nint context);
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint type);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(nint handle, ref Guid id, nint ssid, uint bssType, [MarshalAs(UnmanagedType.Bool)] bool security, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(nint handle, ref Guid id, nint ssid, nint ie, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(nint handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicates, ScanNotification? callback, nint context, nint reserved, out uint previousSource);
}
