namespace ScreenCast.Services.Decoder;

/// <summary>H.264 裸流解码器抽象：把字节流解码为可上屏的 BGRA 像素。</summary>
public interface IFrameDecoder : IDisposable
{
    /// <summary>解码出一帧时触发（已在 UI 线程回调）。参数：BGRA 像素、宽、高。</summary>
    event Action<byte[], int, int>? FrameDecoded;

    /// <summary>送入一段 H.264 Annex-B 裸流。</summary>
    void Feed(ReadOnlyMemory<byte> data);

    void Flush();
}
