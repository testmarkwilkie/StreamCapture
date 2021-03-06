using System;
using System.Diagnostics;
using System.IO;

namespace StreamCapture
{
    public class CaptureProcessInfo
    {
        public Process process { get; set; }
        public DateTime timerDone { get; set;}
        public string outputPath { get; set;}
        public long fileSize { get; set; }
        public long acceptableRate { get; set; }
        public long avgKBytesSec { get; set; }
        public int interval { get; set;}
        public TextWriter logWriter { get; set; }

        public CaptureProcessInfo(Process _p,long _ar,int _i,DateTime _td,string _o,TextWriter _l)
        {
            process=_p;
            acceptableRate=_ar;
            interval=_i;
            timerDone = _td;
            outputPath=_o;
            fileSize=0;
            avgKBytesSec=0;
            logWriter=_l;
        }
    }
}