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

		public void init()
		{
		}

		public void compress(int c) // c is 0..255 or EOF
		{
		}

		public int stat(int x)
		{
			return pr.stat(x);
		}

		public Writer @out;

		private U32 low, high; // range
		private Predictor pr; // to get p
		private Array<char> buf; // unmodeled input
		
		private void encode(int y, int p) // encode bit y (0..1) with prob. p (0..65535)
		{
		}
	}
}
