using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DBImplant_N1toN2.Features.Raw.Parsers
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct CPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class CDrawObject
    {
        public uint m_uiObjectID;
        public uint m_uiCursorType;
        public CPoint m_StartPoint;
        public CPoint m_EndPoint;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string strText;

        public CRect m_CurRect;
        public float m_fFundamentalFreq;
        public float m_fStartFreq;
        public int m_iYPos;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 300)]
        public CPoint[] m_Tick;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 300)]
        public CPoint[] m_TickStartPoint;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 300)]
        public int[] m_Select;

        public uint m_Color;

        public int m_nRatioNum;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public string[] m_arrStrCursorMark;

        public bool m_Set;

        public CDrawObject()
        {
            m_Tick = new CPoint[300];
            m_TickStartPoint = new CPoint[300];
            m_Select = new int[300];
            m_arrStrCursorMark = new string[10];
            m_Set = false;
            strText = string.Empty;
        }
    }

    public static class CDrawObjectReader
    {
        public static CDrawObject Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                var size = Marshal.SizeOf(typeof(CDrawObject));
                var bytes = br.ReadBytes(size);
                var ptr = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.Copy(bytes, 0, ptr, size);
                    return (CDrawObject)Marshal.PtrToStructure(ptr, typeof(CDrawObject));
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        public static List<CDrawObject> ReadDrawingList(string filePath)
        {
            var objects = new List<CDrawObject>();

            // 파일을 열고 바이너리 스트림으로 읽기
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // CDrawObject 크기만큼 데이터를 읽어 객체로 변환
                int objectSize = Marshal.SizeOf(typeof(CDrawObject));

                // 파일 전체 길이를 CDrawObject 크기로 나누어서 객체 수 추정
                long fileLength = fs.Length;
                int objectCount = (int)(fileLength / objectSize);

                for (int i = 0; i < objectCount; i++)
                {
                    byte[] bytes = br.ReadBytes(objectSize);
                    var ptr = Marshal.AllocHGlobal(objectSize);
                    try
                    {
                        // 바이트 배열을 unmanaged 메모리로 복사한 뒤 구조체로 변환
                        Marshal.Copy(bytes, 0, ptr, objectSize);
                        var obj = (CDrawObject)Marshal.PtrToStructure(ptr, typeof(CDrawObject));
                        objects.Add(obj);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }

            return objects;
        }
    }
}
