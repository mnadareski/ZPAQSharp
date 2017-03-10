using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using U8 = System.Byte;
using U16 = System.UInt16;
using U32 = System.UInt32;
using U64 = System.UInt64;

namespace ZPAQSharp
{
	// Encoder compresses using an arithmetic code
	class Encoder
	{
		public Encoder(ZPAQL z, int size = 0)
		{
			@out = null;
			low = 1;
			high = 0xFFFFFFFF;
			pr = new Predictor(z);
		}

		// Initialize for start of block
		public void init()
		{
			low = 1;
			high = 0xFFFFFFFF;
			pr.init();
			if (!pr.isModeled()) low = 0, buf.resize(1 << 16);
		}

		// compress byte c (0..255 or -1=EOS)
		public void compress(int c) // c is 0..255 or EOF
		{
			assert(out);
			if (pr.isModeled())
			{
				if (c == -1)
					encode(1, 0);
				else
				{
					assert(c >= 0 && c <= 255);
					encode(0, 0);
					for (int i = 7; i >= 0; --i)
					{
						int p = pr.predict() * 2 + 1;
						assert(p > 0 && p < 65536);
						int y = c >> i & 1;
						encode(y, p);
						pr.update(y);
					}
				}
			}
			else
			{
				if (low && (c < 0 || low == buf.size()))
				{
      out->put((low >> 24) & 255);
      out->put((low >> 16) & 255);
      out->put((low >> 8) & 255);
      out->put(low & 255);
      out->write(&buf[0], low);
					low = 0;
				}
				if (c >= 0) buf[low++] = c;
			}
		}

		public int stat(int x)
		{
			return pr.stat(x);
		}

		public Writer @out;

		private U32 low, high; // range
		private Predictor pr; // to get p
		private Array<char> buf; // unmodeled input

		// compress bit y having probability p/64K
		private void encode(int y, int p) // encode bit y (0..1) with prob. p (0..65535)
		{
			assert(out);
			assert(p >= 0 && p < 65536);
			assert(y == 0 || y == 1);
			assert(high > low && low > 0);
			U32 mid = low + U32(((high - low) * U64(U32(p))) >> 16);  // split range
			assert(high > mid && mid >= low);
			if (y) high = mid; else low = mid + 1; // pick half
			while ((high ^ low) < 0x1000000)
			{ // write identical leading bytes
    out->put(high >> 24);  // same as low>>24
				high = high << 8 | 255;
				low = low << 8;
				low += (low == 0); // so we don't code 4 0 bytes in a row
			}
		}
	}
}
