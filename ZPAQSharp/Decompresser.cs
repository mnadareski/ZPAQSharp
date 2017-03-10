using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZPAQSharp
{
	// For decompression and listing archive contents
	class Decompresser
	{
		public Decompresser()
		{
			z = new ZPAQL();
			dec = new Decoder(z);
			pp = new PostProcessor();
			state = State.BLOCK;
			decode_state = DecodeState.FIRSTSEG;
		}

		public void setInput(Reader @in)
		{
			dec.@in = @in;
		}

		// Find the start of a block and return true if found. Set memptr
		// to memory used.
		public unsafe bool findBlock(double* memptr = null)
		{
			assert(state == BLOCK);

			// Find start of block
			uint h1 = 0x3D49B113, h2 = 0x29EB7F93, h3 = 0x2614BE13, h4 = 0x3828EB13;
			// Rolling hashes initialized to hash of first 13 bytes
			int c;
			while ((c = dec.get()) != -1)
			{
				h1 = h1 * 12 + c;
				h2 = h2 * 20 + c;
				h3 = h3 * 28 + c;
				h4 = h4 * 44 + c;
				if (h1 == 0xB16B88F1 && h2 == 0xFF5376F1 && h3 == 0x72AC5BF1 && h4 == 0x2F909AF1)
					break;  // hash of 16 byte string
			}
			if (c == -1) return false;

			// Read header
			if ((c = dec.get()) != 1 && c != 2) error("unsupported ZPAQ level");
			if (dec.get() != 1) error("unsupported ZPAQL type");
			z.read(&dec);
			if (c == 1 && z.header.Length > 6 && z.header[6] == 0)
				error("ZPAQ level 1 requires at least 1 component");
			if (memptr) *memptr = z.memory();
			state = FILENAME;
			decode_state = FIRSTSEG;
			return true;
		}

		public void hcomp(Writer out2)
		{
			z.write(out2, false);
		}

		// Read the start of a segment (1) or end of block code (255).
		// If a segment is found, write the filename and return true, else false.
		public bool findFilename(Writer out2 = null)
		{
			assert(state == FILENAME);
			int c = dec.get();
			if (c == 1)
			{  // segment found
				while (true)
				{
					c = dec.get();
					if (c == -1) error("unexpected EOF");
					if (c == 0)
					{
						state = COMMENT;
						return true;
					}
					if (filename) filename.put(c);
				}
			}
			else if (c == 255)
			{  // end of block found
				state = BLOCK;
				return false;
			}
			else
				error("missing segment or end of block");
			return false;
		}

		// Read the comment from the segment header
		public void readComment(Writer out2 = null)
		{
			assert(state == COMMENT);
			state = DATA;
			while (true)
			{
				int c = dec.get();
				if (c == -1) error("unexpected EOF");
				if (c == 0) break;
				if (comment) comment.put(c);
			}
			if (dec.get() != 0) error("missing reserved byte");
		}

		public void setOutput(Writer @out)
		{
			pp.setOutput(@out);
		}

		public void setSHA1(SHA1 sha1ptr)
		{
			pp.setSHA1(sha1ptr);
		}

		// Decompress n bytes, or all if n < 0. Return false if done
		public bool decompress(int n = -1) // n bytes, -1=all, return true until done
		{
			assert(state == DATA);
			if (decode_state == SKIP) error("decompression after skipped segment");
			assert(decode_state != SKIP);

			// Initialize models to start decompressing block
			if (decode_state == FIRSTSEG)
			{
				dec.@init();
				assert(z.header.Length > 5);
				pp.@init(z.header[4], z.header[5]);
				decode_state = SEG;
			}

			// Decompress and load PCOMP into postprocessor
			while ((pp.getState() & 3) != 1)
				pp.write(dec.decompress());

			// Decompress n bytes, or all if n < 0
			while (n)
			{
				int c = dec.decompress();
				pp.write(c);
				if (c == -1)
				{
					state = SEGEND;
					return false;
				}
				if (n > 0) --n;
			}
			return true;
		}

		public bool pcomp(Writer out2)
		{
			return pp.z.write(out2, true);
		}

		// Read end of block. If a SHA1 checksum is present, write 1 and the
		// 20 byte checksum into sha1string, else write 0 in first byte.
		// If sha1string is 0 then discard it.
		public void readSegmentEnd(char[] sha1string = null)
		{
			assert(state == DATA || state == SEGEND);

			// Skip remaining data if any and get next byte
			int c = 0;
			if (state == DATA)
			{
				c = dec.skip();
				decode_state = SKIP;
			}
			else if (state == SEGEND)
				c = dec.get();
			state = FILENAME;

			// Read checksum
			if (c == 254)
			{
				if (sha1string) sha1string[0] = 0;  // no checksum
			}
			else if (c == 253)
			{
				if (sha1string) sha1string[0] = 1;
				for (int i = 1; i <= 20; ++i)
				{
					c = dec.get();
					if (sha1string) sha1string[i] = c;
				}
			}
			else
				error("missing end of segment marker");
		}

		public int stat(int x)
		{
			return dec.stat(x);
		}

		public int buffered()
		{
			return dec.buffered();
		}

		private ZPAQL z;
		private Decoder dec;
		private PostProcessor pp;
		private State state;
		private DecodeState decode_state;

		private enum State // expected next
		{
			BLOCK, FILENAME, COMMENT, DATA, SEGEND
		}

		private enum DecodeState // which segment in block?
		{
			FIRSTSEG, SEG, SKIP
		}
	}
}
