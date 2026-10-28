namespace RawCollectMigrator.Features.DbToDb.Spectrogram
{
    // [찾기:스펙트로그램] AI/TR 스니펫 스펙트로그램 생성 지점
    /// <summary>
    /// 스펙트로그램 한 건의 산출 결과.
    /// 데이터 배치는 NAIMS-II S0 규약을 따른다 — <b>frame-major 평탄 float 배열</b>로
    /// <c>value[frame * BinCount + bin]</c> 순서다(진폭 magnitude, dB 변환 없음).
    /// </summary>
    /// <param name="Amplitude">frame-major 평탄 진폭 배열.</param>
    /// <param name="BinCount">프레임당 주파수 빈 수(S0 규약 801).</param>
    /// <param name="FrameCount">프레임 수.</param>
    /// <param name="StartFreq">첫 빈의 주파수(Hz).</param>
    /// <param name="EndFreq">마지막 빈의 주파수(Hz).</param>
    /// <param name="FramePerSec">초당 프레임 수(= 샘플레이트 / hop).</param>
    public sealed record SpectrogramResult(
        float[] Amplitude, int BinCount, int FrameCount,
        float StartFreq, float EndFreq, float FramePerSec)
    {
        public bool IsEmpty => FrameCount <= 0 || Amplitude.Length == 0;

        /// <summary>DB varbinary 컬럼에 넣을 바이트 배열(float 리틀엔디안 그대로).</summary>
        public byte[] ToBytes()
        {
            var b = new byte[(long)Amplitude.Length * sizeof(float)];
            System.Buffer.BlockCopy(Amplitude, 0, b, 0, b.Length);
            return b;
        }
    }

    /// <summary>
    /// WAV 원음에서 스펙트로그램을 만드는 생성기.
    ///
    /// 구현 교체를 전제로 인터페이스로 분리했다. 현재는 외부 의존이 없는
    /// <see cref="ManagedStftSpectrogramGenerator"/> 를 쓰고, 이후 실제 산출물(.dll)이
    /// 확정되면 그 래퍼 구현으로 갈아끼운다(호출부 수정 없이).
    /// </summary>
    public interface ISpectrogramGenerator
    {
        /// <summary>구현 이름(로그·보고서 표기용).</summary>
        string Name { get; }

        /// <summary>WAV 파일에서 스펙트로그램을 만든다. 프레임을 만들 수 없으면 FrameCount=0 으로 돌려준다.</summary>
        /// <param name="wavPath">원음 WAV 경로.</param>
        /// <param name="frameSize">FFT 프레임 크기(2의 거듭제곱). hop = frameSize/2.</param>
        SpectrogramResult Generate(string wavPath, int frameSize = 2048);
    }
}
