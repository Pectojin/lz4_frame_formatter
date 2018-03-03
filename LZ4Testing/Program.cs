using System;
using System.IO;
using System.Text;
using System.Linq;
using Extensions.Data;
using LZ4;

namespace LZ4Testing
{
    /*
     * Example C# implementation of reading a file and writing files to/from LZ4 format
     * See the specification for details: https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md
     * 
     * The LZ4 frame format does not support multiple files in one LZ4 frame.
     * To work with directories, either put everything in a tarball and compress it
     * Or compress all the files an put them in a tarball.
     * 
     * SharpCompress and SharpZipLib supports tar
     */
    class Program
    {
        static void Main(string[] args)
        {
            // Get user input
            Console.Write("Path to input file: ");
            var user_input = Console.ReadLine();
            var path = new FileInfo(user_input);
            Console.WriteLine("path: " + path.FullName);
            Console.WriteLine("filename: " + path.Name);
            Console.WriteLine("extension: " + path.Extension);

            if (!path.Exists) {
                Console.WriteLine("No file found");
                return;
            }

            // Get input from file
            byte[] input = File.ReadAllBytes(path.FullName);

            if (path.Extension == ".lz4")
            {
                // Decompress .lz4 files
                using (FileStream stream = new FileStream(path.FullName.Replace(".lz4", ""), FileMode.Create))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    Console.WriteLine("####################");
                    Console.WriteLine("Decompressing input...");
                    var result = Decompress(input);
                    writer.Write(result);
                }
            }
            else
            {
                // Compress regular files
                using (FileStream stream = new FileStream("../../lz4_example/db_larger.sqlite.lz4", FileMode.Create))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    Console.WriteLine("####################");
                    Console.Write("Compressing input...");
                    var result = Compress(input);
                    writer.Write(result);

                    Console.WriteLine("####################");
                    Console.WriteLine("Decompressing input...");
                    var t = Decompress(result);

                    // The rest here isn't necessary for compression, but it's useful to confirm
                    // that Compress() and Decompress() agrees on the resulting frame
                    Console.WriteLine("####################");
                    Console.WriteLine("Decompressed result length: " + t.Length);
                    Console.WriteLine("Input result length: " + input.Length);
                    if (t.SequenceEqual(input))
                    {
                        Console.WriteLine("Decompressed result matches input");
                    }
                    else
                    {
                        Console.WriteLine("Something went wrong, input and decompressed output doesn't match");
                    }
                }
            }
        }

        private static byte[] Compress(byte[] input)
        {
            // LZ4 encode the data
            var lz4result = LZ4Codec.Encode(input, 0, input.Length);

            // Check if the compressed data is smaller than the input and continue with the smaller one.
            bool dataIsUncompressed = false;
            if (lz4result.Length > input.Length)
            {
                dataIsUncompressed = true;
                lz4result = input;
            }

            Console.WriteLine("\nData can be compressed: " + !dataIsUncompressed);

            // Calculate the output size (lz4 compressed data + 23 frame bytes + 4 bytes per block size indicator)
            int output_size = lz4result.Length + 23 + ((int)Math.Ceiling((Double)lz4result.Length / 4000000) * 4);
            byte[] output = new byte[output_size];
            Console.WriteLine("Expected output size: " + output_size);

            // The Magic Number (4 bytes - specifically the hex: 0x184D2204)
            byte[] magicNumber = new byte[] { 0b00000100, 0b00100010, 0b01001101, 0b00011000 };
            Array.Copy(magicNumber, 0, output, 0, 4);

            // The Frame Descriptor 
            // -Flags (1 byte)
            // -Version Number (bit 7 and 6 must be 01)
            // -Block Independence flag (bit 5, 1 for independent and 0 for part of a chain of blocks) 
            // -Block Checksum flag (bit 4, on / off)
            // -Content Size flag (bit 3, on / off)
            // -Content checksum flag (bit 2, on / off)
            // -Reserved bit (bit 1, must be 0)
            // -DictID flag (bit 0, on / off)
            // default flags
            byte flags = 0b0100_1100;
            // If the input is smaller than 4MB (the 'max' max block size) we only need 1 block, so we flip the independence flag.
            if (lz4result.Length < 4000000) {
                flags = (byte)(flags | 0b00100000);
            }
            output[4] = flags;


            // BlockDescriptor (1 byte)
            // Reserved bit (bit 7, must be 0)
            // Block Max Size (bit 6-5-4, 111 means 4 MB)
            // Reserved bits (bit 3-2-1-0, must be 0) 
            byte descriptor = 0b0000_0000;
            int maxBlockSize;
            // Determine the max block size for this run and flip the appropriate bits.
            if (lz4result.Length < 64000) {             // 64KB
                descriptor = (byte)(descriptor | 0b01000000);
                maxBlockSize = 64000;
            } else if (lz4result.Length < 256000) {     // 256KB
                descriptor = (byte)(descriptor | 0b01010000);
                maxBlockSize = 256000;
            } else if (lz4result.Length < 1000000) {    // 1MB
                descriptor = (byte)(descriptor | 0b01100000);
                maxBlockSize = 1000000;
            } else {                                    // 4MB
                descriptor = (byte)(descriptor | 0b01110000);
                maxBlockSize = 4000000;
            }
            output[5] = descriptor;

            // Content Size - Original uncompressed size (0 - 8 bytes)
            // Not enabled by default in LZ4 on Ubuntu, must use --content-size flag with the LZ4 binary
            // to make a compatible file (because our LZ4 Codec needs to know the content size)
            byte[] contentSize = BitConverter.GetBytes(input.LongLength);
            Array.Copy(contentSize, 0, output, 6, 8);
            Console.WriteLine("Content size: " + BitConverter.ToInt32(contentSize, 0) + " bytes");

            // Dictionary ID (0 - 4 bytes)
            // Not enabled by default in LZ4 on Ubuntu and we don't need it

            // Header Checksum - Checksum on the combined Frame Descriptor (1 byte)
            // Combine flags, descriptor, and content size into one variable, hash it, then get the 2nd byte.
            byte[] header = new byte[10];
            header[0] = flags;
            header[1] = descriptor;
            Array.Copy(contentSize, 0, header, 2, 8);
            // Get 2nd byte of XXHash32 - xxh32() : (xxh32()>>8) & 0xFF
            var headerChecksum = (XXHash.XXH32(header) >> 8) & 0xFF;

            output[14] = BitConverter.GetBytes(headerChecksum)[0];

            // Figure out how many blocks we need and how large the remainder block is
            int remainder;
            int blocks = Math.DivRem(lz4result.Length, maxBlockSize, out remainder);
            // account for the remainder block
            blocks++;

            Console.WriteLine("Compressed content size: " + lz4result.Length + " bytes");
            Console.WriteLine("Divided into " + blocks + " blocks, last block size: " + remainder + " bytes");

            // Data Blocks 
            // Block Size (4 bytes)
            // Must be smaller than Block Maximum Size.
            // Heighest bit (little-endian) determines if data is compressed or not, we have to flip this manually. 
            int dataOffset = 0;
            int blockOffset = 0;
            for (int i = 1; i <= blocks; i++) {
                byte[] blockSizeArray;
                if (i == blocks) {
                     blockSizeArray = BitConverter.GetBytes(remainder);
                } else {
                    blockSizeArray = BitConverter.GetBytes(maxBlockSize);
                }

                int blockSize = BitConverter.ToInt32(blockSizeArray, 0);

                // Flip the highest bit to 1 if data is uncompressed. Leave it as 0 if data is compressed.
                if (dataIsUncompressed)
                {
                    blockSizeArray[3] = (byte)(blockSizeArray[3] | 0b10000000);
                }
                Array.Copy(blockSizeArray, 0, output, (15+dataOffset+blockOffset), 4);

                blockOffset += 4;

                // Data
                Array.Copy(lz4result, dataOffset, output, (15 + dataOffset + blockOffset), blockSize);

                // Block Checksum (0-4 bytes)
                // Not implemented in LZ4 on Ubuntu, we'll just rely on content checksum

                // Increment the offset
                if (i == blocks) {
                    dataOffset = dataOffset + remainder; 
                } else {
                    dataOffset = dataOffset + maxBlockSize;
                }
            }

            // EndMark (4 bytes - a 32 bit integer with the value of 0)
            Int32 endMark = 0;
            Array.Copy(BitConverter.GetBytes(endMark), 0, output, 15 + dataOffset + blockOffset, 4);

            // Checksum (0-4 bytes)
            uint contentChecksum = XXHash.XXH32(input, 0);
            Array.Copy(BitConverter.GetBytes(contentChecksum), 0, output, 19 + dataOffset + blockOffset, 4);

            return output;
        }

        private static byte[] Decompress(byte[] input)
        {
            // First parse the magic number and verify it
            if (BitConverter.ToInt32(input, 0) != 0x184D2204)
            {
                throw new InvalidOperationException("Magic number incorrect");
            }

            // Parse flags
            byte flags = input[4];
            bool version = ((flags | 0b00000001) >> 6) == 1;
            bool blockIndependenceFlag = ((flags & 0b00100000) >> 5) == 1;
            bool blockChecksumFlag = ((flags & 0b00010000) >> 4) == 1;
            bool contentSizeFLag = ((flags & 0b00001000) >> 3) == 1;
            bool contentChecksumFlag = ((flags & 0b00000100) >> 2) == 1;

            // Parse block descriptor
            byte descriptor = input[5];
            int blockMaxSize = (descriptor >> 4);

            // Parse content size
            byte[] contentSizeArray = new byte[8];
            Array.Copy(input, 6, contentSizeArray, 0, 8);
            int contentSize = BitConverter.ToInt32(contentSizeArray, 0);
            Console.WriteLine("Expected content size: " + contentSize);

            // Parse header checksum and validate it
            byte headerChecksum = input[14];
            byte[] header = new byte[10];
            header[0] = flags;
            header[1] = descriptor;
            Array.Copy(contentSizeArray, 0, header, 2, 8);

            // Calculate checksum with parsed header values
            var calculatedHeaderChecksum = (XXHash.XXH32(header) >> 8) & 0xFF;
            if (headerChecksum != calculatedHeaderChecksum)
            {
                throw new InvalidOperationException("Header doesn't match checksum");
            }

            int dataOffset = 0;
            int blockOffset = 0;
            var blocks = 0;
            // Not entirely true, we're guaranteed that the data size is equal to or smaller than contentSize
            var data = new byte[contentSize];
            bool dataIsUncompressed = false;
            while (true) {
                // Parse the block size
                byte[] blockSizeArray = new byte[4];
                Array.Copy(input, 15 + dataOffset + blockOffset, blockSizeArray, 0, 4);

                // break when blockSize is 0 (that's the EndMark)
                if (BitConverter.ToInt32(blockSizeArray, 0) == 0) break;

                blocks++;
                // Check if the data is compressed by inspecting the highest bit - 0 for compressed, 1 for uncompressed
                if ((blockSizeArray[3] & 0b10000000) == 0b10000000) {
                    // uncompressed
                    dataIsUncompressed = true;
                    // flip the bit back
                    blockSizeArray[3] = (byte)(blockSizeArray[3] ^ 0b10000000);
                }

                int blockSize = BitConverter.ToInt32(blockSizeArray, 0);
                //Console.WriteLine("Block size: " + blockSize);
                blockOffset += 4;

                // Data
                Array.Copy(input, 15 + dataOffset + blockOffset, data, 0 + dataOffset, blockSize);

                // Block Checksum (0-4 bytes)
                // Not implemented in LZ4 on Ubuntu, we'll just rely on content checksum

                // Increment the offset
                dataOffset += blockSize;
            }

            Console.WriteLine("Data is compressed: " + !dataIsUncompressed);

            Console.WriteLine("Content size: " + dataOffset);

            // If data is uncompressed just return the content
            var lz4result = new byte[contentSize];
            if (dataIsUncompressed)
            {
                lz4result = data;
            }
            else // Use the LZ4 Codec to decode our block
            {
                lz4result = LZ4Codec.Decode(data, 0, dataOffset, contentSize);
                //lz4result = data;
            }

            // Parse end mark
            int endMark = BitConverter.ToInt32(input, input.Length - 8);

            // Parse content checksum
            uint contentChecksum = BitConverter.ToUInt32(input, input.Length - 4);

            // Validate the content against the content checksum
            uint calculatedContentChecksum = XXHash.XXH32(lz4result, 0);

            if (contentChecksum != calculatedContentChecksum)
            {
                Console.WriteLine("Content checksum: " + contentChecksum);
                Console.WriteLine("Calculted content checksum: " + calculatedContentChecksum);
                throw new InvalidOperationException("Content doesn't match checksum");
            }

            return lz4result;
        }

        // Helper method to construct a string representation of a byte array for printing in terminal
        private static string StringifyByteArray(byte[] input)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in input)
            {
                sb.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
            }
            return sb.ToString();
        }

        // Helper method to construct a string representation of a byte for printing in terminal
        private static string StringifyByte(byte input)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Convert.ToString(input, 2).PadLeft(8, '0'));
            return sb.ToString();
        }
    }
}
