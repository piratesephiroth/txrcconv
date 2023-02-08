using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


namespace txrcconv
{
    internal class Program
    {
        // Suikoden Tierkeris and Love Plus: encode whole file, little endian
        // CVHD: keeps raw header after encoding, big endian

        const string headerStartMarker = "----------HEADER START----------";
        const string headerEndMarker = "-----------HEADER END-----------";
        const string indexListStartMarker = "---------INDEXES START----------";
        const string indexListEndMarker = "----------INDEXES END-----------";
        const string textStartMarker = "-----------TEXT START-----------";
        const string textEndMarker = "------------TEXT END------------";

        static void Main(string[] args)
        {

            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var inputItems = new List<string>();
            foreach (string arg in args)
            {
                // if the file exists, add to list
                if (File.Exists(arg))
                {
                    inputItems.Add(arg);
                }
            }


            foreach (string item in inputItems)
            {
                string baseDir = Directory.GetParent(item).FullName;

                if (Path.GetExtension(item).Equals(".txrc", StringComparison.InvariantCultureIgnoreCase))
                {
                    BinaryReader txrcFile = new BinaryReader(File.Open(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    string txrcText = TxrcToText(txrcFile);

                    string outputFile = Path.GetFileName(item) + ".txt";
                    File.WriteAllText(baseDir + Path.DirectorySeparatorChar + outputFile, txrcText);
                    txrcFile.Close();
                    Console.WriteLine($"Done: {Path.GetFileName(item)} -> {outputFile}");
                }


                else if (Path.GetExtension(item).Equals(".txt", StringComparison.InvariantCultureIgnoreCase))
                {
                    string[] txrcText = File.ReadAllLines(item);
                    byte[] txrcBytes = TextToTxrc(txrcText);

                    string outputFile = Path.GetFileName(item) + ".txrc";

                    FileMode openMode;
                    if (File.Exists(outputFile))
                        openMode = FileMode.Truncate;
                    else
                        openMode = FileMode.CreateNew;
                    BinaryWriter outFile = new BinaryWriter(File.Open(baseDir + Path.DirectorySeparatorChar + outputFile,
                                                            openMode,
                                                            FileAccess.Write));
                    outFile.Write(txrcBytes);
                    outFile.Close();
                    Console.WriteLine($"Done: {Path.GetFileName(item)} -> {outputFile}");
                }

                else
                {
                    Console.WriteLine($"ERROR: {Path.GetFileName(item)} isn't a .txrc or .txt file.");
                }

            }
            Console.WriteLine("");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }



        static string TxrcToText(BinaryReader inputTxrc)
        {
            // decode the txrc
            byte[] encodedBytes = inputTxrc.ReadBytes((int)inputTxrc.BaseStream.Length);
            byte[] decodedBytes = Scramble(encodedBytes);

            bool isBigEndian = false;

            if (encodedBytes[0x0c] == 0)
            {   // CVHD doesn't scramble the header
                // so restore it from the original file
                uint headerSize = ReadUIntFromByteArray(encodedBytes, 0x18, true);
                Buffer.BlockCopy(encodedBytes, 0, decodedBytes, 0, (int)headerSize);
                isBigEndian = true;
            }

            uint tablePosition = ReadUIntFromByteArray(decodedBytes, 0x14, isBigEndian);
            uint textPosition = ReadUIntFromByteArray(decodedBytes, 0x18, isBigEndian);

            StringBuilder headerLines = new StringBuilder();
            StringBuilder tableLines = new StringBuilder();
            StringBuilder textLines = new StringBuilder();
            StringBuilder decodedTxrc = new StringBuilder();

            // convert header bytes to lines of hex text
            headerLines.AppendLine(headerStartMarker);
            for (int i = 0; i < tablePosition; i += 0x10)
            {
                headerLines.AppendLine(ByteArrayToHexString(decodedBytes, i, 0x10));
            }
            headerLines.AppendLine(headerEndMarker);


            // handle table and strings
            uint tableEntry;
            uint stringSize;
            uint stringPosition;
            uint extend = 0;
            var uniqueEntries = new List<uint>();
            var orderedIndexes = new List<int>();

            textLines.AppendLine(textStartMarker);

            for (uint pos = tablePosition; pos < textPosition; pos += 4)
            {
                tableEntry = ReadUIntFromByteArray(decodedBytes, (int)pos, isBigEndian);

                if (!uniqueEntries.Contains(tableEntry))
                {
                    uniqueEntries.Add(tableEntry);

                    stringSize = ((tableEntry >> 16) & 0xFFFF) >> 6;
                    stringPosition = (tableEntry & 0xFFFF) + textPosition + extend;

                    // bad position or size indicates we're reading padding at end of table, skip
                    if (stringPosition > decodedBytes.Length || (stringSize > (decodedBytes.Length - stringPosition)))
                    {
                        continue;
                    }

                    // if string length breaks the 65535 bytes limit, extend the limit
                    if ((tableEntry & 0xFFFF) + stringSize > 0xFFFF)
                    {
                        extend += 0x10000;
                    }

                    // read string bytes
                    byte[] stringBytes = new byte[stringSize];
                    Buffer.BlockCopy(decodedBytes, (int)stringPosition, stringBytes, 0, (int)stringSize);
                    // convert to string 
                    string decodedString = Encoding.UTF8.GetString(stringBytes);
                    decodedString = decodedString.Replace("\r", "\\r");
                    decodedString = decodedString.Replace("\n", "\\n");

                    textLines.AppendLine(decodedString);
                }

                orderedIndexes.Add(uniqueEntries.IndexOf(tableEntry));
            }
            textLines.AppendLine(textEndMarker);

            // write table of indexes, pack 4 per line to save space
            tableLines.AppendLine(indexListStartMarker);
            int count = 0;
            string currLine = "";

            for (int i = 0; i < orderedIndexes.Count; i++)
            {
                currLine += orderedIndexes[i].ToString("X" + 4);
                count++;
                // break line every 8 indexes or at the final one, adding 0s as padding
                if (count == 8 || i == orderedIndexes.Count - 1)
                {
                    tableLines.AppendLine(currLine.PadRight(16, '0'));
                    currLine = "";
                    count = 0;
                }
            }
            tableLines.Append(indexListEndMarker);


            // assemble final text
            decodedTxrc.Append(textLines);
            decodedTxrc.Append(headerLines);
            decodedTxrc.Append(tableLines);

            return decodedTxrc.ToString().TrimEnd();
        }


        static byte[] TextToTxrc(string[] decodedTextLines)
        {
            byte[] headerBuffer = new byte[0x10000];
            byte[] tempIndexBuffer = new byte[0x10000];
            byte[] stringBuffer = new byte[0x100000];
            int headerBlockPos = 0;
            int tempIndexSize = 0;
            int textBlockPos = 0;
            var orderedIndexes = new List<int>();

            uint entryInfo = 0;
            var uniqueEntries = new List<uint>();
            byte[] currentLineBytes;
            bool isBigEndian = false;


            // read the lines and get the sections
            for (int lineIdx = 0; lineIdx < decodedTextLines.Length; lineIdx++)
            {
                switch (decodedTextLines[lineIdx])
                {
                    case headerStartMarker:
                        lineIdx++;
                        while (decodedTextLines[lineIdx] != headerEndMarker)
                        {
                            currentLineBytes = HexStringToByteArray(decodedTextLines[lineIdx]);
                            Buffer.BlockCopy(currentLineBytes, 0, headerBuffer, headerBlockPos, currentLineBytes.Length);
                            headerBlockPos += currentLineBytes.Length;
                            lineIdx++;
                        }

                        // CVHD doesn't scramble the header
                        if (headerBuffer[0x0c] == 0)
                        {
                            isBigEndian = true;
                        }
                        break;

                    case indexListStartMarker:
                        lineIdx++;
                        // read indexes into byte array
                        while (decodedTextLines[lineIdx] != indexListEndMarker)
                        {
                            currentLineBytes = HexStringToByteArray(decodedTextLines[lineIdx]);
                            Buffer.BlockCopy(currentLineBytes, 0, tempIndexBuffer, tempIndexSize, currentLineBytes.Length);
                            tempIndexSize += currentLineBytes.Length;
                            lineIdx++;
                        }
                        // and use that to build the list of indexes
                        for (int i = 0; i < tempIndexSize; i += 2)
                        {
                            orderedIndexes.Add((int)ReadUShortFromByteArray(tempIndexBuffer, i, true));
                        }
                        break;

                    case textStartMarker:
                        lineIdx++;
                        while (decodedTextLines[lineIdx] != textEndMarker)
                        {
                            // get string
                            string decodedString = decodedTextLines[lineIdx];
                            decodedString = decodedString.Replace("\\r", "\r");
                            decodedString = decodedString.Replace("\\n", "\n");
                            currentLineBytes = Encoding.UTF8.GetBytes(decodedString);
                            Buffer.BlockCopy(currentLineBytes, 0, stringBuffer, textBlockPos, currentLineBytes.Length);

                            // and build list of unique entries
                            if (currentLineBytes.Length == 0)
                                entryInfo = 0;
                            else
                                entryInfo = (uint)(currentLineBytes.Length << (6 + 16)) | (uint)textBlockPos;


                            uniqueEntries.Add(entryInfo);

                            textBlockPos += currentLineBytes.Length;
                            lineIdx++;
                        }
                        break;
                }

            }

            // write pointers to headerbuffer
            foreach (int i in orderedIndexes)
            {
                WriteUIntToByteArray(headerBuffer, (int)headerBlockPos, uniqueEntries[i], isBigEndian);
                headerBlockPos += 4;
            }

            // advance position, to add padding bytes
            if (headerBlockPos % 16 != 0)
                headerBlockPos += 16 - (headerBlockPos % 16);
            if (tempIndexSize % 16 != 0)
                tempIndexSize += 16 - (tempIndexSize % 16);
            if (textBlockPos % 16 != 0)
                textBlockPos += (ushort)(16 - (textBlockPos % 16));

            // fix table length ???
            WriteUIntToByteArray(headerBuffer, 0x18, (uint)(headerBlockPos), isBigEndian);

            // build the final txrc byte array
            byte[] decodedBytes = new byte[headerBlockPos + textBlockPos];
            Buffer.BlockCopy(headerBuffer, 0, decodedBytes, 0, (int)headerBlockPos);
            Buffer.BlockCopy(stringBuffer, 0, decodedBytes, (int)headerBlockPos, (int)textBlockPos);

            byte[] encodedBytes = Scramble(decodedBytes);

            // for CVHD
            if (isBigEndian)
            {
                Buffer.BlockCopy(decodedBytes, 0, encodedBytes, 0, headerBlockPos);
            }
            return encodedBytes;
        }

        static byte[] Scramble(byte[] inputBytes)
        {
            // get the key
            byte[] key = new byte[4];
            Buffer.BlockCopy(inputBytes, 4, key, 0, 4);

            // copy magic and key to output
            byte[] outputBytes = new byte[inputBytes.Length];
            Buffer.BlockCopy(inputBytes, 0, outputBytes, 0, 8);

            byte n1;
            byte n2;
            for (uint i = 8; i < inputBytes.Length; i++)
            {
                n1 = inputBytes[i];
                n2 = (byte)(key[i % 3] + (i / 3) * key[3]);
                outputBytes[i] = (byte)(n1 ^ n2);
            }

            return outputBytes;
        }

        static uint ReadUIntFromByteArray(byte[] b, int pos, bool isBigEndian = false)
        {
            byte[] num = new byte[4];
            Buffer.BlockCopy(b, pos, num, 0, 4);

            if (BitConverter.IsLittleEndian && isBigEndian)
                Array.Reverse(num);

            return BitConverter.ToUInt32(num, 0);
        }

        static ushort ReadUShortFromByteArray(byte[] byteArray, int pos, bool isBigEndian = false)
        {
            byte[] num = new byte[2];
            Buffer.BlockCopy(byteArray, pos, num, 0, 2);

            if (BitConverter.IsLittleEndian && isBigEndian)
                Array.Reverse(num);

            return BitConverter.ToUInt16(num, 0);
        }

        static void WriteUShortToByteArray(byte[] byteArray, int pos, UInt16 value, bool isBigEndian = false)
        {
            byte[] num = BitConverter.GetBytes(value);

            if (BitConverter.IsLittleEndian && isBigEndian)
                Array.Reverse(num);

            Buffer.BlockCopy(num, 0, byteArray, pos, 2);

            return;
        }

        static void WriteUIntToByteArray(byte[] byteArray, int pos, UInt32 value, bool isBigEndian = false)
        {
            byte[] num = BitConverter.GetBytes(value);

            if (BitConverter.IsLittleEndian && isBigEndian)
                Array.Reverse(num);

            Buffer.BlockCopy(num, 0, byteArray, pos, 4);

            return;
        }


        public static string ByteArrayToHexString(byte[] Bytes, int offset, int length)
        {
            StringBuilder Result = new StringBuilder(Bytes.Length * 2);
            string HexAlphabet = "0123456789ABCDEF";
            byte B;

            for(int i = offset; i < offset + length; i++)
            {
                B = Bytes[i];
                Result.Append(HexAlphabet[(int)(B >> 4)]);
                Result.Append(HexAlphabet[(int)(B & 0xF)]);
            }

            return Result.ToString();
        }

        public static byte[] HexStringToByteArray(string Hex)
        {
            byte[] Bytes = new byte[Hex.Length / 2];
            int[] HexValue = new int[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
               0x06, 0x07, 0x08, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
               0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };

            for (int x = 0, i = 0; i < Hex.Length; i += 2, x += 1)
            {
                Bytes[x] = (byte)(HexValue[Char.ToUpper(Hex[i + 0]) - '0'] << 4 |
                                  HexValue[Char.ToUpper(Hex[i + 1]) - '0']);
            }

            return Bytes;
        }

        static void PrintUsage()
        {
            string exename = Path.GetFileName(System.Reflection.Assembly.GetEntryAssembly().Location);
            Console.WriteLine("This tool can convert .txrc files to .txt and then back to .txrc");
            Console.WriteLine("It accepts multiple input files at the same time.");
            Console.WriteLine("The converted files are created next to the original ones.");
            Console.WriteLine("");
            Console.WriteLine("Usage: " + exename + " inputfile1 <inputfile2> <inputfile3> ...");
            Console.WriteLine("");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

    }
}