using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.Supplement
{
    // =========================
    // .alg Models (필요한 만큼 확장 가능)
    // =========================
    public sealed class SupplementSheet
    {
        public SupplementMissionInfo MissionInfo { get; set; } = new();
        public List<SupplementRecmedia> Recmedia { get; } = new();

        public int SetCount { get; set; }

        public List<SupplementNaviSeaInfo> NaviSeaInfos { get; } = new();
        public List<SupplementNoiseInfo> NoiseInfos { get; } = new();
        public List<SupplementTargetInfo> TargetInfos { get; } = new();
        public List<SupplementNbDetectionInfo> NbDetectionInfos { get; } = new();
        public List<SupplementAiDetectionInfo> AiDetectionInfos { get; } = new();
        public List<SupplementTrDetectionInfo> TrDetectionInfos { get; } = new();
        public List<SupplementImageInfo> ImageInfos { get; } = new();
    }

    public sealed class SupplementMissionInfo
    {
        public string DetPlatform { get; set; } = "";
        public string DetVesselClass { get; set; } = "";
        public string DetVesselName { get; set; } = "";
        public string MissionDateFr { get; set; } = "";
        public string MissionDateTo { get; set; } = "";
        public string MissionSeaArea { get; set; } = "";
        public string MissionSummary { get; set; } = "";
        public string MissionComment { get; set; } = "";
    }

    public sealed class SupplementRecmedia
    {
        public int RecordCount { get; set; }
        public List<SupplementRecord> Records { get; } = new();
    }

    public sealed class SupplementRecord
    {
        public string RecordTimeFr { get; set; } = "";
        public string RecordTimeTo { get; set; } = "";
        public string RecordComment { get; set; } = "";
    }

    public sealed class SupplementNaviSeaInfo
    {
        public string DetDate { get; set; } = "";
        public string DetTime { get; set; } = "";
        public int Depth { get; set; }
        public int TADepth { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string DetPath { get; set; } = "";
        public double Speed { get; set; }
        public string Situation { get; set; } = "";
        public double WaveHeight { get; set; }
        public double RgnDepth { get; set; }

        public int TemperatureCount { get; set; }
        public List<SupplementTemperature> Temperatures { get; } = new();
    }

    public sealed class SupplementTemperature
    {
        public double Depth { get; set; }
        public double Temperature { get; set; }
        public double Sonic { get; set; }
        public double Salt { get; set; }
        public double GradeDepth { get; set; }
        public string Comment { get; set; } = "";
    }

    public sealed class SupplementNoiseInfo
    {
        public int MachineCount { get; set; }
        public List<SupplementMachine> Machines { get; } = new();
    }

    public sealed class SupplementMachine
    {
        public string MachineName { get; set; } = "";
        public string RunTimeFr { get; set; } = "";
        public string RunTimeTo { get; set; } = "";
        public double OccurenceFreq { get; set; }
        public string Comment { get; set; } = "";
    }

    public sealed class SupplementTargetInfo
    {
        public int TargetCount { get; set; }
        public List<SupplementTarget> Targets { get; } = new();
    }

    public sealed class SupplementTarget
    {
        public int TargetNumber { get; set; }
        public string ContactRecTimeFr { get; set; } = "";
        public string ContactRecTimeTo { get; set; } = "";
        public string ClassifyMethod { get; set; } = "";
        public string DetSonar { get; set; } = "";
        public string Sensitivity { get; set; } = "";
        public string Preamplifier { get; set; } = "";
        public string AGC { get; set; } = "";
        public string RecmediaNumber { get; set; } = "";
        public string AFTFWD { get; set; } = "";
        public string TrackChannel { get; set; } = "";
        public string RawsignalNumber { get; set; } = "";
        public string RawsignalName { get; set; } = "";
        public double FrequencyBand { get; set; }
        public double SampleRate { get; set; }
        public string Nation { get; set; } = "";
        public string TargetType { get; set; } = "";
        public string TargetClass { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string NaviMode { get; set; } = "";
        public double TargetDistance { get; set; }
        public double TargetSpeed { get; set; }
        public string EngineType { get; set; } = "";
        public string DetsignalType { get; set; } = "";
    }

    public sealed class SupplementNbDetectionInfo
    {
        public int NbDetinfoCount { get; set; }
        public List<SupplementNbDetection> NbDetections { get; } = new();
    }

    public sealed class SupplementNbDetection
    {
        public int TargetNumber { get; set; }
        public string DetTimeFr { get; set; } = "";
        public string DetTimeTo { get; set; } = "";
        public string DetVesselClass { get; set; } = "";
        public string DetVesselName { get; set; } = "";
        public string TargetClassify { get; set; } = "";
        public double Distance { get; set; }
        public double Speed { get; set; }
        public string AnalysisResultFile { get; set; } = "";

        // TYPE I
        public double FeatureFreq { get; set; }
        public double SuperiorFreq { get; set; }
        public int CylinderCount { get; set; }
        public double BR { get; set; }
        public double PSR { get; set; }
        public double PSRPM { get; set; }
        public double TSR { get; set; }
        public double CFR { get; set; }
        public double CSR { get; set; }
        public double EFR { get; set; }
        public double ERPM { get; set; }
        public string Machine { get; set; } = "";
        public double SWATH { get; set; }
        public string OddSignal { get; set; } = "";
        public double LP { get; set; }
        public double Pitch { get; set; }
        public string DetSide { get; set; } = "";
        public string Comment { get; set; } = "";

        // TYPE II
        public int IndexCount { get; set; }
        public List<SupplementNbIndex> Indices { get; } = new();
    }

    public sealed class SupplementNbIndex
    {
        public int Index { get; set; }
        public double CenterFreq { get; set; }
        public double NormalFreq { get; set; }
        public int Harmonic { get; set; }
        public string NoiseSrc { get; set; } = "";
    }

    public sealed class SupplementAiDetectionInfo
    {
        public int AiDetinfoCount { get; set; }
        public List<SupplementAiDetection> AiDetections { get; } = new();
    }

    public sealed class SupplementAiDetection
    {
        public int TargetNumber { get; set; }
        public int IndexCount { get; set; }
        public List<SupplementAiIndex> Indices { get; } = new();
    }

    public sealed class SupplementAiIndex
    {
        public int Index { get; set; }
        public string DetTimeFr { get; set; } = "";
        public string DetTimeTo { get; set; } = "";
        public string SonarName { get; set; } = "";
        public string PulseName { get; set; } = "";
        public double Distance { get; set; }
        public double Size { get; set; }
        public string AnalysisResultFile { get; set; } = "";

        public int SegmentCount { get; set; }
        public List<SupplementAiSegment> Segments { get; } = new();
    }

    public sealed class SupplementAiSegment
    {
        public int Index { get; set; }
        public double StartFreq { get; set; }
        public double EndFreq { get; set; }
        public double SegmentSize { get; set; }
        public double Modulation { get; set; }
    }

    public sealed class SupplementTrDetectionInfo
    {
        public int TrDetinfoCount { get; set; }
        public List<SupplementTrDetection> TrDetections { get; } = new();
    }

    public sealed class SupplementTrDetection
    {
        public int TargetNumber { get; set; }
        public int IndexCount { get; set; }
        public List<SupplementTrIndex> Indices { get; } = new();
    }

    public sealed class SupplementTrIndex
    {
        public int Index { get; set; }
        public string DetTimeFr { get; set; } = "";
        public string DetTimeTo { get; set; } = "";
        public string TransientClassify { get; set; } = "";
        public string TransientName { get; set; } = "";
        public double Distance { get; set; }
        public double Size { get; set; }
        public string AnalysisResultFile { get; set; } = "";
    }

    public sealed class SupplementImageInfo
    {
        public int ImageCount { get; set; }
        public List<SupplementImage> Images { get; } = new();
    }

    public sealed class SupplementImage
    {
        public int TargetNumber { get; set; }
        public string Nation { get; set; } = "";
        public string VesselClass { get; set; } = "";
        public string VesselName { get; set; } = "";
        public string ImageFile { get; set; } = "";
        public string Comment { get; set; } = "";
    }
}
