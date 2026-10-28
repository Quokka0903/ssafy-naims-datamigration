using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;

namespace DBImplant_N1toN2.Features.Migration.Views
{
    public partial class GramCompareView : UserControl
    {
        public GramCompareView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// .grm 파일 경로를 받아 레거시/신규 비교 전시를 생성한다.
        /// </summary>
        public void LoadGrm(string grmFilePath)
        {
            try
            {
                var parsed = GrmFileReader.Read(grmFilePath);
                if (parsed.Error != null)
                {
                    TxtInfo.Text = $"파싱 오류: {parsed.Error}";
                    return;
                }

                int width, height;
                short[] shortBuf;
                float[] floatBuf;

                bool isNewFormat = false;

                if (parsed.FileType == LegacyGrmParseResult.GrmFileType.NewFormat && parsed.NewData != null && parsed.Data != null)
                {
                    // 레거시 short[] 포맷이지만 NewFormat으로 파싱됨 — 비교 가능
                    width = parsed.Data.BinCount;
                    height = parsed.Data.FrameCount;
                    shortBuf = parsed.Data.ShortBuffer;
                    floatBuf = parsed.Data.FloatBuffer;
                    isNewFormat = false; // 레거시/신규 비교 모드
                }
                else if (parsed.FileType == LegacyGrmParseResult.GrmFileType.NewFormat && parsed.NewData != null)
                {
                    // 순수 신규 체계 .grm — float[] 원본 (역스케일링 불필요)
                    width = parsed.NewData.BinCount;
                    height = parsed.NewData.FrameCount;
                    floatBuf = parsed.NewData.FloatBuffer;
                    shortBuf = Array.Empty<short>();
                    isNewFormat = true;
                }
                else if (parsed.FileType == LegacyGrmParseResult.GrmFileType.RawHeaderless && parsed.RawData != null)
                {
                    width = parsed.RawData.BinCount;
                    height = parsed.RawData.FrameCount;
                    shortBuf = parsed.RawData.ShortBuffer;
                    floatBuf = parsed.RawData.FloatBuffer;
                }
                else if (parsed.Data != null)
                {
                    width = parsed.Data.BinCount;
                    height = parsed.Data.FrameCount;
                    shortBuf = parsed.Data.ShortBuffer;
                    floatBuf = parsed.Data.FloatBuffer;
                }
                else
                {
                    TxtInfo.Text = "데이터 없음";
                    return;
                }

                if (width <= 0 || height <= 0)
                {
                    TxtInfo.Text = $"빈 데이터: {width}×{height}";
                    return;
                }

                if (isNewFormat)
                {
                    // 신규 포맷: float[] 원본 → ×150+40 전시 (이미 신규 포맷이므로 변환 불필요)
                    TxtLabelTop.Text = $"신규 체계 원본 [{parsed.NewData?.Header}] (float→×150+40→Green)";
                    TxtLabelBottom.Text = "동일 (변환 불필요 — 이미 신규 포맷)";
                    byte[] newPixels = GrmFileReader.ToNewBgra32(floatBuf, width, height);
                    ImgLegacy.Source = CreateBitmap(newPixels, width, height);
                    ImgNew.Source = CreateBitmap(newPixels, width, height);
                }
                else
                {
                    // 레거시 전시: 원본 BYTE → Green 채널
                    byte[] legacyPixels = GrmFileReader.ToLegacyBgra32(shortBuf, width, height);
                    ImgLegacy.Source = CreateBitmap(legacyPixels, width, height);

                    // 신규 전시: float → ×150+40 → clamp → Green 채널
                    byte[] newPixels = GrmFileReader.ToNewBgra32(floatBuf, width, height);
                    ImgNew.Source = CreateBitmap(newPixels, width, height);
                }

                // 차이 계산 (신규 포맷은 차이 없음)
                int diffCount = 0;
                int pixelCount = floatBuf.Length;
                if (!isNewFormat && shortBuf.Length > 0)
                {
                    pixelCount = Math.Min(shortBuf.Length, floatBuf.Length);
                    for (int i = 0; i < pixelCount; i++)
                    {
                        byte legacy = (byte)(shortBuf[i] & 0xFF);
                        float scaled = floatBuf[i] * 150f + 40f;
                        byte newVal = (byte)Math.Clamp((int)Math.Round(scaled), 0, 255);
                        if (legacy != newVal) diffCount++;
                    }
                }

                string typeStr = parsed.FileType switch
                {
                    LegacyGrmParseResult.GrmFileType.NewFormat => $"New[{parsed.NewData?.Header}] {parsed.NewData?.BinCount}bin",
                    LegacyGrmParseResult.GrmFileType.RawHeaderless => $"Raw[{parsed.RawData?.GramSubType}]",
                    LegacyGrmParseResult.GrmFileType.NbSnippet => $"NB[{parsed.NbHeader?.GramType}]",
                    LegacyGrmParseResult.GrmFileType.AiSnippet => "AI",
                    _ => "Unknown"
                };

                string diffStr = isNewFormat
                    ? "신규 포맷 (변환 불필요)"
                    : $"차이: {diffCount}px ({(double)diffCount / Math.Max(pixelCount, 1):P2})";

                TxtInfo.Text = $"{typeStr} | {width}bin × {height}frame | 총 {pixelCount:N0}px | {diffStr}";
            }
            catch (Exception ex)
            {
                TxtInfo.Text = $"예외: {ex.Message}";
            }
        }

        private static WriteableBitmap CreateBitmap(byte[] bgra32, int width, int height)
        {
            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            int stride = width * 4;
            bmp.WritePixels(new Int32Rect(0, 0, width, height), bgra32, stride, 0);
            return bmp;
        }
    }
}
