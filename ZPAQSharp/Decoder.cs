using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using byte = System.Byte;
using ushort = System.UInt16;
using uint = System.UInt32;
using ulong = System.UInt64;

namespace ZPAQSharp
{
	// Decoder decompresses using an arithmetic code
	class Decoder : Reader
	{
		public Reader @in; // destination
		
		public Decoder(ZPAQL z)
		{
			@in = null;
			low = 1;
			high = 0xFFFFFFFF;
			curr = 0;
			rpos = 0;
			wpos = 0;
			pr = new Predictor(z);
			buf = new char[]((ulong)BUFSIZE);
		}

		public int decompress() // return a byte or EOF
		{
			if (pr.isModeled())
			{  // n>0 components?
				if (curr == 0)
				{  // segment initialization
					for (int i = 0; i < 4; ++i)
						curr = curr << 8 | get();
				}
				if (decode(0))
				{
					if (curr != 0) error("decoding end of stream");
					return -1;
				}
				else
				{
					int c = 1;
					while (c < 256)
					{  // get 8 bits
						int p = pr.predict() * 2 + 1;
						c += c + decode(p);
						pr.update(c & 1);
					}
					return c - 256;
				}
			}
			else
			{
				if (curr == 0)
				{
					for (int i = 0; i < 4; ++i) curr = curr << 8 | get();
					if (curr == 0) return -1;
				}
				--curr;
				return get();
			}
		}

		public int skip() // skip to the end of the segment, return next byte
		{
			nt c = -1;
			if (pr.isModeled())
			{
				while (curr == 0)  // at start?
					curr = get();
				while (curr && (c = get()) >= 0)  // find 4 zeros
					curr = curr << 8 | c;
				while ((c = get()) == 0) ;  // might be more than 4
				return c;
			}
			else
			{
				if (curr == 0)  // at start?
					for (int i = 0; i < 4 && (c = get()) >= 0; ++i) curr = curr << 8 | c;
				while (curr > 0)
				{
					while (curr > 0)
					{
						--curr;
						if (get() < 0) return error("skipped to EOF"), -1;
					}
					for (int i = 0; i < 4 && (c = get()) >= 0; ++i) curr = curr << 8 | c;
				}
				if (c >= 0) c = get();
				return c;
			}
		}

		public void init() // initialize at start of block
		{
			pr.@init();
			if (pr.isModeled()) low = 1, high = 0xFFFFFFFF, curr = 0;
			else low = high = curr = 0;
		}

		public int stat(int x)
		{
			return pr.stat(x);
		}

		public override int get() // return 1 byte of buffered input or EOF
		{
			if (rpos == wpos)
			{
				rpos = 0;
				wpos = @in != null ? @in.read(buf, BUFSIZE) : 0;
				Debug.Assert(wpos <= BUFSIZE);
			}

			return rpos < wpos ? (byte)buf[rpos++] : -1;
		}

		public int buffered() // how far read ahead?
		{
			return (int)(wpos - rpos);
		}

		private uint low, high;     // range
		private uint curr;          // last 4 bytes of archive or remaining bytes in subblock
		private uint rpos, wpos;    // read, write position in buf
		private Predictor pr;      // to get p
		private int BUFSIZE = 1 << 16;
		private char[] buf;   // input buffer of size BUFSIZE bytes

		private int decode(int p)  // return decoded bit (0..1) with prob. p (0..65535)
		{
			assert(pr.isModeled());
			assert(p >= 0 && p < 65536);
			assert(high > low && low > 0);
			if (curr < low || curr > high) error("archive corrupted");
			assert(curr >= low && curr <= high);
			uint mid = low + uint(((high - low) * ulong(uint(p))) >> 16);  // split range
			assert(high > mid && mid >= low);
			int y;
			if (curr <= mid) y = 1, high = mid;  // pick half
  else y = 0, low = mid + 1;
			while ((high ^ low) < 0x1000000)
			{ // shift out identical leading bytes
				high = high << 8 | 255;
				low = low << 8;
				low += (low == 0);
				int c = get();
				if (c < 0) error("unexpected end of file");
				curr = curr << 8 | c;
			}
			return y;
		}
	}
}
