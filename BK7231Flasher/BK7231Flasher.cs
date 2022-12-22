﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Windows.Forms;
namespace BK7231Flasher
{
    public enum BKType
    {
        BK7231T,
        BK7231N
    }
    public class BK7231Flasher
    {
        bool bDebugUART;
        SerialPort serial;
        ILogListener logger;
        BKType chipType = BKType.BK7231N;

        uint[] crc32_table;
        uint crc32_ver2(uint crc, byte[] buffer)
        {
            for (uint i = 0; i < buffer.Length; i++)
            {
                uint c = buffer[i];
                crc = (crc >> 8) ^ crc32_table[(crc ^ c) & 0xff];
            }
            return crc;
        }
        void addLog(string s)
        {
            logger.addLog(s, Color.Black);
        }
        void addError(string s)
        {
            logger.addLog(s, Color.Red);
        }
        void addSuccess(string s)
        {
            logger.addLog(s, Color.Green);
        }
        public BK7231Flasher(ILogListener logger, SerialPort serial)
        {
            this.logger = logger;
            this.serial = serial;

            crc32_table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((c & 1) != 0)
                    {
                        c = (0xEDB88320 ^ (c >> 1));
                    }
                    else
                    {
                        c = c >> 1;
                    }
                }
                crc32_table[i] = c;
            }
        }
        enum CommandCode
        {
            LinkCheck = 0,
            FlashRead4K = 0x09,
            CheckCRC = 0x10,
        }
        byte[] BuildCmd_LinkCheck()
        {
            byte[] ret = new byte[5];
            ret[0] = 0x01;
            ret[1] = 0xe0;
            ret[2] = 0xfc;
            ret[3] = 0x01; // len
            ret[4] = (byte)CommandCode.LinkCheck;
            return ret;
        }
        byte[] BuildCmd_CheckCRC(int startAddr, int endAddr)
        {
            int length = 1 + (4 + 4);
            byte[] buf = new byte[13];
            buf[0] = 0x01;
            buf[1] = 0xe0;
            buf[2] = 0xfc;
            buf[3] = (byte)length;
            buf[4] = (byte)CommandCode.CheckCRC;
            buf[5] = (byte)(startAddr & 0xff);
            buf[6] = (byte)((startAddr >> 8) & 0xff);
            buf[7] = (byte)((startAddr >> 16) & 0xff);
            buf[8] = (byte)((startAddr >> 24) & 0xff);
            buf[9] = (byte)(endAddr & 0xff);
            buf[10] = (byte)((endAddr >> 8) & 0xff);
            buf[11] = (byte)((endAddr >> 16) & 0xff);
            buf[12] = (byte)((endAddr >> 24) & 0xff);
            return buf;
        }
        byte[] BuildCmd_FlashRead4K(int addr)
        {
            int length = 1 + (4 + 0);
            byte[] ret = new byte[12];
            ret[0] = 0x01;
            ret[1] = 0xe0;
            ret[2] = 0xfc;
            ret[3] = 0xff;
            ret[4] = 0xf4;
            ret[5] = (byte)(length & 0xff);
            ret[6] = (byte)((length >> 8) & 0xff);
            ret[7] = (byte)CommandCode.FlashRead4K;
            ret[8] = (byte)(addr & 0xff);
            ret[9] = (byte)((addr >> 8) & 0xff);
            ret[10] = (byte)((addr >> 16) & 0xff);
            ret[11] = (byte)((addr >> 24) & 0xff);
            return ret;
        }
        int CalcRxLength_CheckCRC()
        {
            return (3 + 3 + 1 + 4);
        }
        int CalcRxLength_LinkCheck()
        {
            return (3 + 3 + 1 + 1 + 0);
        }
        void consumeSerial(float timeout)
        {
            int realRead;
            serial.ReadTimeout = (int)(1000 * timeout);
            byte[] tmp = new byte[4096];
            try
            {
                realRead = serial.Read(tmp, 0, tmp.Length);
            }
            catch (Exception ex)
            {

            }
        }
        byte[] tmp = new byte[4096];
        void consumePending()
        {
            if (serial.BytesToRead > 0)
            {
                serial.Read(tmp, 0, serial.BytesToRead);
            }
        }
        byte[] Start_Cmd(byte[] txbuf, int rxLen = 0, float timeout = 0.05f)
        {
            consumePending();
            int realRead = 0;
            serial.ReadTimeout = 10;
            serial.Write(txbuf, 0, txbuf.Length);
            var timer = new Stopwatch();
            timer.Start();
            if (rxLen > 0)
            {
                byte[] ret = new byte[rxLen];
                while (timer.Elapsed.TotalSeconds < timeout)
                {
                    try
                    {
                        //addLog("serial.BytesToRead " + serial.BytesToRead+"");
                        if (serial.BytesToRead >= rxLen)
                        {
                            //  addLog("Tries to read!");
                            int readNow = serial.Read(ret, realRead, rxLen - realRead);
                            realRead += readNow;
                            if(bDebugUART)
                            {
                                addLog("Read len: " + realRead + Environment.NewLine);
                            }
                            if (realRead == rxLen)
                            {
                                if (bDebugUART)
                                {
                                    addLog("Got UART reply!" + Environment.NewLine);
                                }
                                return ret;
                            }
                        }
                    }
                    catch (TimeoutException)
                    {

                    }
                    catch (Exception ex)
                    {
                        addLog("Got exception: " + ex.ToString() + "!" + Environment.NewLine);
                        return null;
                    }
                }
                if (rxLen > 10)
                {
                    addLog("failed with serial.BytesToRead " + serial.BytesToRead + "" + Environment.NewLine);
                }
                return null;
            }
            return null;
        }
        static bool ByteArrayCompare(byte[] a1, byte[] a2, int len)
        {
            if (a1.Length < len)
                return false;
            if (a2.Length < len)
                return false;
            for (int i = 0; i < len; i++)
                if (a1[i] != a2[i])
                    return false;

            return true;
        }
        static bool ByteArrayCompare(byte[] a1, byte[] a2)
        {
            if (a1.Length != a2.Length)
                return false;

            for (int i = 0; i < a1.Length; i++)
                if (a1[i] != a2[i])
                    return false;

            return true;
        }
        uint CheckRespond_CheckCRC(byte[] buf, int a0, int a1)
        {
            byte[] cBuf = new byte[] { 0x04, 0x0e, 0x05, 0x01, 0xe0, 0xfc, (byte)CommandCode.CheckCRC };
            cBuf[2] = 3 + 1 + 4;
            if (cBuf.Length <= buf.Length && ByteArrayCompare(cBuf, buf, cBuf.Length))
            {
                //addLog("CheckRespond_CheckCRC: OK");
                uint r = buf[10];
                r = (r << 8) + buf[9];
                r = (r << 8) + buf[8];
                r = (r << 8) + buf[7];
                return r;
            }
            addLog("CheckRespond_CheckCRC: ERROR" + Environment.NewLine);
            return 0;
        }
        bool CheckRespond_FlashRead4K(byte[] buf, int addr)
        {
            byte[] cBuf = new byte[] { 0x04, 0x0e, 0xff, 0x01, 0xe0, 0xfc, 0xf4, (1 + 1 + (4 + 4 * 1024)) & 0xff,
                ((1 + 1 + (4 + 4 * 1024)) >> 8) & 0xff, (byte)CommandCode.FlashRead4K};
            if (cBuf.Length <= buf.Length && ByteArrayCompare(cBuf, buf, cBuf.Length))
            {
                // addLog("CheckRespond_FlashRead4K: OK");
                return true;
            }
            addLog("CheckRespond_FlashRead4K: ERROR" + Environment.NewLine);
            return false;
        }

        bool CheckRespond_LinkCheck(byte[] buf)
        {
            byte[] cBuf = new byte[] { 0x04, 0x0e, 0x05, 0x01, 0xe0, 0xfc, (byte)(CommandCode.LinkCheck) + 1, 0x00 };
            return cBuf.Length <= buf.Length && ByteArrayCompare(cBuf, buf);
        }
        bool getBus()
        {
            int maxTries = 10;
            int loops = 1000;
            bool bOk = false;
            addLog("Getting bus... (now, please do reboot by CEN or by power off/on)" + Environment.NewLine);
            for (int tr = 0; tr < maxTries && !bOk; tr++)
            {
                for (int l = 0; l < loops && !bOk; l++)
                {
                    bOk = linkCheck();
                    if (bOk)
                    {
                        addSuccess("Getting bus success!" + Environment.NewLine);
                        return true;
                    }
                }
                addError("Getting bus failed, will try again - " + tr + "/" + maxTries + "!" + Environment.NewLine);
            }
            return false;
        }
        string formatHex(int i)
        {
            return "0x" + i.ToString("X2");
        }
        string formatHex(uint i)
        {
            return "0x" + i.ToString("X2");
        }
        public void doRead()
        {
            try
            {
                doReadInternal();
            }
            catch(Exception ex)
            {
                addError("Exception caught: " + ex.ToString());
            }
        }
        void doReadInternal() { 
            addLog(Environment.NewLine + "Starting read!" + Environment.NewLine);
            addLog("Now is: " +DateTime.Now.ToLongDateString() + " "+  DateTime.Now.ToLongTimeString() + "." + Environment.NewLine);
            addLog("Using serial port: "+serial.PortName+ "." + Environment.NewLine);
            if (getBus() == false)
            {
                addError("Failed to get bus!" + Environment.NewLine);
                return;
            }
            Thread.Sleep(100);

            addLog("Flasher mode: " + chipType +  Environment.NewLine);
            MemoryStream ms;
            ms = new MemoryStream();
            int startSector = 0x000;
            int step = 4096;
            int sectors = 10;
            // 4K page align
            startSector = (int)(startSector & 0xfffff000);
            addLog("Going to start reading at offset " + formatHex(startSector) + "..." + Environment.NewLine);
            for (int i = 0; i < sectors; i++)
            {
                int addr = startSector + step * i;
                addLog("Reading " + formatHex(addr) + "... ");
                bool bOk = readSectorTo(addr, ms);
                if (bOk == false)
                {
                    addError("Failed! ");
                    return;
                }
                addLog("Ok! ");
            }
            int total = step * sectors;
            addSuccess("All read!" + Environment.NewLine);
            addLog("Loaded total " + formatHex(total) + " bytes " + Environment.NewLine);
            int last = startSector + total;
            if(chipType == BKType.BK7231N)
            {
                last = last - 1;
            }
            uint bk_crc = calcCRC(startSector, last);
            uint our_crc = crc32_ver2(0xffffffff, ms.ToArray());
            if(bk_crc != our_crc)
            {
                addError("CRC mismatch!" + Environment.NewLine);
                addError("Send by BK " + formatHex(bk_crc) + ", our CRC " + formatHex(our_crc) + Environment.NewLine);
                addError("Maybe you have wrong chip type set?" + Environment.NewLine);
            }
            else
            {
                addSuccess("CRC matches " + formatHex(bk_crc) + "!" + Environment.NewLine);
            }
            File.WriteAllBytes("lastRead.bin", ms.ToArray());
        }
        bool readSectorTo(int addr, MemoryStream tg)
        {
            byte[] res = readSector(addr);
            if (res != null)
            {
                int start_ofs = 15;
                tg.Write(res, start_ofs, res.Length - start_ofs);
                return true;
            }
            return false;
        }
        int CalcRxLength_FlashRead4K()
        {
            return (3 + 3 + 3 + (1 + 1 + (4 + 4 * 1024)));
        }
        byte[] readSector(int addr)
        {
            //addLog("Starting read sector for " + addr + Environment.NewLine);
            byte[] txbuf = BuildCmd_FlashRead4K(addr);
            byte[] rxbuf = Start_Cmd(txbuf, CalcRxLength_FlashRead4K(), 5);
            if (rxbuf != null)
            {
                //addLog("Loaded " + rxbuf.Length + " bytes!" + Environment.NewLine);
                if (CheckRespond_FlashRead4K(rxbuf, addr))
                {
                    return rxbuf;
                }
            }
            //addLog("Failed!" + Environment.NewLine);
            return null;
        }
        uint calcCRC(int start, int end)
        {
            byte[] txbuf = BuildCmd_CheckCRC(start, end);
            byte[] rxbuf = Start_Cmd(txbuf, CalcRxLength_CheckCRC(), 5.0f);
            if (rxbuf != null)
            {
                uint r = CheckRespond_CheckCRC(rxbuf, start, end);
                return r;
            }
            return 0;
        }
        bool linkCheck()
        {
            byte[] txbuf = BuildCmd_LinkCheck();
            byte[] rxbuf = Start_Cmd(txbuf, CalcRxLength_LinkCheck(), 0.001f);
            if (rxbuf != null)
            {
                if (CheckRespond_LinkCheck(rxbuf))
                {
                    return true;
                }
            }
            return false;
        }
    }
}