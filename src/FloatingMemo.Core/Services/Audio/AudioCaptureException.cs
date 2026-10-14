namespace FloatingMemo.Services.Audio;

/// <summary>A capture device could not be opened or failed while recording.</summary>
public sealed class AudioCaptureException : Exception
{
    // HRESULTs seen when opening WASAPI devices.
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);
    internal const int E_NOTFOUND = unchecked((int)0x80070490);              // no default device
    internal const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004); // unplugged
    internal const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);  // exclusive use by another app

    public AudioCaptureException(AudioSources source, Exception? inner)
        : base(MessageFor(source, inner), inner)
    {
        Source = source;
    }

    public new AudioSources Source { get; }

    private static string MessageFor(AudioSources source, Exception? inner)
    {
        string what = source == AudioSources.Microphone ? "マイクを開けませんでした" : "PC音声を取得できませんでした";
        string device = source == AudioSources.Microphone ? "マイク" : "再生デバイス（スピーカー/ヘッドホン）";

        string? hint = inner switch
        {
            UnauthorizedAccessException => PrivacyHint(source),
            { HResult: E_ACCESSDENIED } => PrivacyHint(source),
            AudioDeviceUnavailableException => inner.Message,
            { HResult: E_NOTFOUND } => $"{device}が見つかりません。接続を確認してください。",
            { HResult: AUDCLNT_E_DEVICE_INVALIDATED } => $"{device}が取り外されたか、無効になりました。",
            { HResult: AUDCLNT_E_DEVICE_IN_USE } => $"{device}は他のアプリが占有しています。",
            null => null,
            _ => inner.Message.Trim(),
        };

        return hint is null ? what : $"{what}（{hint}）";
    }

    private static string PrivacyHint(AudioSources source) => source == AudioSources.Microphone
        ? "マイクへのアクセスが許可されていません。Windows の［設定］>［プライバシーとセキュリティ］>［マイク］を確認してください。"
        : "アクセスが拒否されました。";
}

/// <summary>The chosen device is missing or disabled (the message says which one).</summary>
public sealed class AudioDeviceUnavailableException(string message) : Exception(message);
