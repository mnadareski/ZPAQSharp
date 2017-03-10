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
	class Predictor
	{
		public Predictor(ZPAQL z)
		{
			c8 = 1;
			hmap4 = 1;
			this.z = z;
			assert(sizeof(byte) == 1);
			assert(sizeof(ushort) == 2);
			assert(sizeof(uint) == 4);
			assert(sizeof(ulong) == 8);
			assert(sizeof(short) == 2);
			assert(sizeof(int) == 4);
			pcode = 0;
			pcode_size = 0;
			initTables = false;
		}

		~Predictor()
		{
			allocx(pcode, pcode_size, 0);  // free executable memory
		}

		// Initialize the predictor with a new model in z
		public unsafe void init() // build model
		{
			// Clear old JIT code if any
			allocx(pcode, pcode_size, 0);

			// Initialize context hash function
			z.@inith();

			// Initialize model independent tables
			if (!initTables && isModeled())
			{
				initTables = true;
				memcpy(dt2k, sdt2k, sizeof(dt2k));
				memcpy(dt, sdt, sizeof(dt));

				// ssquasht[i]=int(32768.0/(1+exp((i-2048)*(-1.0/64))));
				// Copy middle 1344 of 4096 entries.
				memset(squasht, 0, 1376 * 2);
				memcpy(squasht + 1376, ssquasht, 1344 * 2);
				for (int i = 2720; i < 4096; ++i) squasht[i] = 32767;

				// sstretcht[i]=int(log((i+0.5)/(32767.5-i))*64+0.5+100000)-100000;
				int k = 16384;
				for (int i = 0; i < 712; ++i)
					for (int j = stdt[i]; j > 0; --j)
						stretcht[k++] = i;
				assert(k == 32768);
				for (int i = 0; i < 16384; ++i)
					stretcht[i] = -stretcht[32767 - i];

# ifndef NDEBUG
				// Verify floating point math for squash() and stretch()
				uint sqsum = 0, stsum = 0;
				for (int i = 32767; i >= 0; --i)
					stsum = stsum * 3 + stretch(i);
				for (int i = 4095; i >= 0; --i)
					sqsum = sqsum * 3 + squash(i - 2048);
				assert(stsum == 3887533746u);
				assert(sqsum == 2278286169u);
#endif
			}

			// Initialize predictions
			for (int i = 0; i < 256; ++i) h[i] = p[i] = 0;

			// Initialize components
			for (int i = 0; i < 256; ++i)  // clear old model
				comp[i].@init();
			int n = z.header[6]; // hsize[0..1] hh hm ph pm n (comp)[n] END 0[128] (hcomp) END
			const byte* cp = &z.header[7];  // start of component list
			for (int i = 0; i < n; ++i)
			{
				assert(cp < &z.header[z.cend]);
				assert(cp > &z.header[0] && cp < &z.header[z.header.Length - 8]);
				Component cr = comp[i];
				switch (cp[0])
				{
					case CONS:  // c
						p[i] = (cp[1] - 128) * 4;
						break;
					case CM: // sizebits limit
						if (cp[1] > 32) error("max size for CM is 32");
						Array.Resize(ref cr.cm, 1, cp[1]);  // packed CM (22 bits) + CMCOUNT (10 bits)
						cr.limit = cp[2] * 4;
						for (size_t j = 0; j < cr.cm.Length; ++j)
							cr.cm[j] = 0x80000000;
						break;
					case ICM: // sizebits
						if (cp[1] > 26) error("max size for ICM is 26");
						cr.limit = 1023;
						Array.Resize(ref cr.cm, 256);
						Array.Resize(ref cr.ht, 64, cp[1]);
						for (size_t j = 0; j < cr.cm.Length; ++j)
							cr.cm[j] = st.cminit(j);
						break;
					case MATCH:  // sizebits
						if (cp[1] > 32 || cp[2] > 32) error("max size for MATCH is 32 32");
						Array.Resize(ref cr.cm, 1, cp[1]);  // index
						Array.Resize(ref cr.ht, 1, cp[2]);  // buf
						cr.ht(0) = 1;
						break;
					case AVG: // j k wt
						if (cp[1] >= i) error("AVG j >= i");
						if (cp[2] >= i) error("AVG k >= i");
						break;
					case MIX2:  // sizebits j k rate mask
						if (cp[1] > 32) error("max size for MIX2 is 32");
						if (cp[3] >= i) error("MIX2 k >= i");
						if (cp[2] >= i) error("MIX2 j >= i");
						cr.c = (size_t(1) << cp[1]); // size (number of contexts)
						Array.Resize(ref cr.a16, 1, cp[1]);  // wt[size][m]
						for (size_t j = 0; j < cr.a16.Length; ++j)
							cr.a16[j] = 32768;
						break;
					case MIX:
						{  // sizebits j m rate mask
							if (cp[1] > 32) error("max size for MIX is 32");
							if (cp[2] >= i) error("MIX j >= i");
							if (cp[3] < 1 || cp[3] > i - cp[2]) error("MIX m not in 1..i-j");
							int m = cp[3];  // number of inputs
							assert(m >= 1);
							cr.c = (size_t(1) << cp[1]); // size (number of contexts)
							Array.Resize(ref cr.cm, m, cp[1]);  // wt[size][m]
							for (size_t j = 0; j < cr.cm.Length; ++j)
								cr.cm[j] = 65536 / m;
							break;
						}
					case ISSE:  // sizebits j
						if (cp[1] > 32) error("max size for ISSE is 32");
						if (cp[2] >= i) error("ISSE j >= i");
						Array.Resize(ref cr.ht, 64, cp[1]);
						Array.Resize(ref cr.cm, 512);
						for (int j = 0; j < 256; ++j)
						{
							cr.cm[j * 2] = 1 << 15;
							cr.cm[j * 2 + 1] = clamp512k(stretch(st.cminit(j) >> 8) * 1024);
						}
						break;
					case SSE: // sizebits j start limit
						if (cp[1] > 32) error("max size for SSE is 32");
						if (cp[2] >= i) error("SSE j >= i");
						if (cp[3] > cp[4] * 4) error("SSE start > limit*4");
						Array.Resize(ref cr.cm, 32, cp[1]);
						cr.limit = cp[4] * 4;
						for (size_t j = 0; j < cr.cm.Length; ++j)
							cr.cm[j] = squash((j & 31) * 64 - 992) << 17 | cp[3];
						break;
					default: error("unknown component type");
				}
				assert(compsize[*cp] > 0);
				cp += compsize[*cp];
				assert(cp >= &z.header[7] && cp < &z.header[z.cend]);
			}
		}

		// Return a prediction of the next bit in range 0..32767
		// Use JIT code starting at pcode[0] if available, or else create it.
		public int predict() // probability that next bit is a 1 (0..4095)
		{
# ifdef NOJIT
			return predict0();
#else
			if (!pcode)
			{
				allocx(pcode, pcode_size, (z.cend * 100 + 4096) & -4096);
				int n = assemble_p();
				if (n > pcode_size)
				{
					allocx(pcode, pcode_size, n);
					n = assemble_p();
				}
				if (!pcode || n < 15 || pcode_size < 15)
					error("run JIT failed");
			}
			assert(pcode && pcode[0]);
			return ((int(*)(Predictor)) & pcode[10])(this);
#endif
		}

		// Update the model with bit y = 0..1
		// Use the JIT code starting at pcode[5].
		public void update(int y) // train on bit y (0..1)
		{
# ifdef NOJIT
			update0(y);
#else
			assert(pcode && pcode[5]);
			((void(*)(Predictor, int)) & pcode[5])(this, y);

			// Save bit y in c8, hmap4 (not implemented in JIT)
			c8 += c8 + y;
			if (c8 >= 256)
			{
				z.run(c8 - 256);
				hmap4 = 1;
				c8 = 1;
				for (int i = 0; i < z.header[6]; ++i) h[i] = z.H(i);
			}
			else if (c8 >= 16 && c8 < 32)
				hmap4 = (hmap4 & 0xf) << 5 | y << 4 | 1;
			else
				hmap4 = (hmap4 & 0x1f0) | (((hmap4 & 0xf) * 2 + y) & 0xf);
#endif
		}

		public int stat(int x) // defined externally
		{
			return 0;
		}

		public bool isModeled() // n>0 components?
		{
			Debug.Assert(z.header.Length > 6);
			return z.header[6] != 0;
		}

		// Predictor state
		private int c8;               // last 0...7 bits.
		private int hmap4;            // c8 split into nibbles
		private int[] p = new int[256];           // predictions
		private uint[] h = new uint[256];           // unrolled copy of z.h
		private ZPAQL z;              // VM to compute context hashes, includes H, n
		private Component[] comp = new Component[256];  // the model, includes P
		private bool initTables;      // are tables initialized?

		// Modeling support functions
		private int predict0() // default
		{
			ssert(initTables);
			assert(c8 >= 1 && c8 <= 255);

			// Predict next bit
			int n = z.header[6];
			assert(n > 0 && n <= 255);
			const byte* cp = &z.header[7];
			assert(cp[-1] == n);
			for (int i = 0; i < n; ++i)
			{
				assert(cp > &z.header[0] && cp < &z.header[z.header.Length - 8]);
				Component cr = comp[i];
				switch (cp[0])
				{
					case CONS:  // c
						break;
					case CM:  // sizebits limit
						cr.cxt = h[i] ^ hmap4;
						p[i] = stretch(cr.cm(cr.cxt) >> 17);
						break;
					case ICM: // sizebits
						assert((hmap4 & 15) > 0);
						if (c8 == 1 || (c8 & 0xf0) == 16) cr.c = find(cr.ht, cp[1] + 2, h[i] + 16 * c8);
						cr.cxt = cr.ht[cr.c + (hmap4 & 15)];
						p[i] = stretch(cr.cm(cr.cxt) >> 8);
						break;
					case MATCH: // sizebits bufbits: a=len, b=offset, c=bit, cxt=bitpos,
								//                   ht=buf, limit=pos
						assert(cr.cm.Length == (size_t(1) << cp[1]));
						assert(cr.ht.Length == (size_t(1) << cp[2]));
						assert(cr.a <= 255);
						assert(cr.c == 0 || cr.c == 1);
						assert(cr.cxt < 8);
						assert(cr.limit < cr.ht.Length);
						if (cr.a == 0) p[i] = 0;
						else
						{
							cr.c = (cr.ht(cr.limit - cr.b) >> (7 - cr.cxt)) & 1; // predicted bit
							p[i] = stretch(dt2k[cr.a] * (cr.c * -2 + 1) & 32767);
						}
						break;
					case AVG: // j k wt
						p[i] = (p[cp[1]] * cp[3] + p[cp[2]] * (256 - cp[3])) >> 8;
						break;
					case MIX2:
						{ // sizebits j k rate mask
						  // c=size cm=wt[size] cxt=input
							cr.cxt = ((h[i] + (c8 & cp[5])) & (cr.c - 1));
							assert(cr.cxt < cr.a16.Length);
							int w = cr.a16[cr.cxt];
							assert(w >= 0 && w < 65536);
							p[i] = (w * p[cp[2]] + (65536 - w) * p[cp[3]]) >> 16;
							assert(p[i] >= -2048 && p[i] < 2048);
						}
						break;
					case MIX:
						{  // sizebits j m rate mask
						   // c=size cm=wt[size][m] cxt=index of wt in cm
							int m = cp[3];
							assert(m >= 1 && m <= i);
							cr.cxt = h[i] + (c8 & cp[5]);
							cr.cxt = (cr.cxt & (cr.c - 1)) * m; // pointer to row of weights
							assert(cr.cxt <= cr.cm.Length - m);
							int* wt = (int*)&cr.cm[cr.cxt];
							p[i] = 0;
							for (int j = 0; j < m; ++j)
								p[i] += (wt[j] >> 8) * p[cp[2] + j];
							p[i] = clamp2k(p[i] >> 8);
						}
						break;
					case ISSE:
						{ // sizebits j -- c=hi, cxt=bh
							assert((hmap4 & 15) > 0);
							if (c8 == 1 || (c8 & 0xf0) == 16)
								cr.c = find(cr.ht, cp[1] + 2, h[i] + 16 * c8);
							cr.cxt = cr.ht[cr.c + (hmap4 & 15)];  // bit history
							int* wt = (int*)&cr.cm[cr.cxt * 2];
							p[i] = clamp2k((wt[0] * p[cp[2]] + wt[1] * 64) >> 16);
						}
						break;
					case SSE:
						{ // sizebits j start limit
							cr.cxt = (h[i] + c8) * 32;
							int pq = p[cp[2]] + 992;
							if (pq < 0) pq = 0;
							if (pq > 1983) pq = 1983;
							int wt = pq & 63;
							pq >>= 6;
							assert(pq >= 0 && pq <= 30);
							cr.cxt += pq;
							p[i] = stretch(((cr.cm(cr.cxt) >> 10) * (64 - wt) + (cr.cm(cr.cxt + 1) >> 10) * wt) >> 13);
							cr.cxt += wt >> 5;
						}
						break;
					default:
						error("component predict not implemented");
				}
				cp += compsize[cp[0]];
				assert(cp < &z.header[z.cend]);
				assert(p[i] >= -2048 && p[i] < 2048);
			}
			assert(cp[0] == NONE);
			return squash(p[n - 1]);
		}

		// Update model with decoded bit y (0...1)
		private void update0(int y) // default
		{
			assert(initTables);
			assert(y == 0 || y == 1);
			assert(c8 >= 1 && c8 <= 255);
			assert(hmap4 >= 1 && hmap4 <= 511);

			// Update components
			const byte* cp = &z.header[7];
			int n = z.header[6];
			assert(n >= 1 && n <= 255);
			assert(cp[-1] == n);
			for (int i = 0; i < n; ++i)
			{
				Component cr = comp[i];
				switch (cp[0])
				{
					case CONS:  // c
						break;
					case CM:  // sizebits limit
						train(cr, y);
						break;
					case ICM:
						{ // sizebits: cxt=ht[b]=bh, ht[c][0..15]=bh row, cxt=bh
							cr.ht[cr.c + (hmap4 & 15)] = st.next(cr.ht[cr.c + (hmap4 & 15)], y);
							uint & pn = cr.cm(cr.cxt);
							pn += int(y * 32767 - (pn >> 8)) >> 2;
						}
						break;
					case MATCH: // sizebits bufbits:
								//   a=len, b=offset, c=bit, cm=index, cxt=bitpos
								//   ht=buf, limit=pos
						{
							assert(cr.a <= 255);
							assert(cr.c == 0 || cr.c == 1);
							assert(cr.cxt < 8);
							assert(cr.cm.Length == (size_t(1) << cp[1]));
							assert(cr.ht.Length == (size_t(1) << cp[2]));
							assert(cr.limit < cr.ht.Length);
							if (int(cr.c) != y) cr.a = 0;  // mismatch?
							cr.ht(cr.limit) += cr.ht(cr.limit) + y;
							if (++cr.cxt == 8)
							{
								cr.cxt = 0;
								++cr.limit;
								cr.limit &= (1 << cp[2]) - 1;
								if (cr.a == 0)
								{  // look for a match
									cr.b = cr.limit - cr.cm(h[i]);
									if (cr.b & (cr.ht.Length - 1))
										while (cr.a < 255
											   && cr.ht(cr.limit - cr.a - 1) == cr.ht(cr.limit - cr.a - cr.b - 1))
											++cr.a;
								}
								else cr.a += cr.a < 255;
								cr.cm(h[i]) = cr.limit;
							}
						}
						break;
					case AVG:  // j k wt
						break;
					case MIX2:
						{ // sizebits j k rate mask
						  // cm=wt[size], cxt=input
							assert(cr.a16.Length == cr.c);
							assert(cr.cxt < cr.a16.Length);
							int err = (y * 32767 - squash(p[i])) * cp[4] >> 5;
							int w = cr.a16[cr.cxt];
							w += (err * (p[cp[2]] - p[cp[3]]) + (1 << 12)) >> 13;
							if (w < 0) w = 0;
							if (w > 65535) w = 65535;
							cr.a16[cr.cxt] = w;
						}
						break;
					case MIX:
						{   // sizebits j m rate mask
							// cm=wt[size][m], cxt=input
							int m = cp[3];
							assert(m > 0 && m <= i);
							assert(cr.cm.Length == m * cr.c);
							assert(cr.cxt + m <= cr.cm.Length);
							int err = (y * 32767 - squash(p[i])) * cp[4] >> 4;
							int* wt = (int*)&cr.cm[cr.cxt];
							for (int j = 0; j < m; ++j)
								wt[j] = clamp512k(wt[j] + ((err * p[cp[2] + j] + (1 << 12)) >> 13));
						}
						break;
					case ISSE:
						{ // sizebits j  -- c=hi, cxt=bh
							assert(cr.cxt == cr.ht[cr.c + (hmap4 & 15)]);
							int err = y * 32767 - squash(p[i]);
							int* wt = (int*)&cr.cm[cr.cxt * 2];
							wt[0] = clamp512k(wt[0] + ((err * p[cp[2]] + (1 << 12)) >> 13));
							wt[1] = clamp512k(wt[1] + ((err + 16) >> 5));
							cr.ht[cr.c + (hmap4 & 15)] = st.next(cr.cxt, y);
						}
						break;
					case SSE:  // sizebits j start limit
						train(cr, y);
						break;
					default:
						assert(0);
				}
				cp += compsize[cp[0]];
				assert(cp >= &z.header[7] && cp < &z.header[z.cend]
					   && cp < &z.header[z.header.Length - 8]);
			}
			assert(cp[0] == NONE);

			// Save bit y in c8, hmap4
			c8 += c8 + y;
			if (c8 >= 256)
			{
				z.run(c8 - 256);
				hmap4 = 1;
				c8 = 1;
				for (int i = 0; i < n; ++i) h[i] = z.H(i);
			}
			else if (c8 >= 16 && c8 < 32)
				hmap4 = (hmap4 & 0xf) << 5 | y << 4 | 1;
			else
				hmap4 = (hmap4 & 0x1f0) | (((hmap4 & 0xf) * 2 + y) & 0xf);
		}

		private int[] dt2k = new int[256]; // division table for match: dt2k[i] = 2^12/i
		private int[] dt = new int[1024]; // division table for cm: dt[i] = 2^16/(i+1.5)
		private ushort[] squasht = new ushort[4096]; // squash() lookup table
		private short[] stretcht = new short[32768]; // stretch() lookup table
		private StateTable st; // next, cminit functions
		private byte[] pcode; // JIT code for predict() and update()
		private int pcode_size; // length of pcode

		// reduce prediction error in cr.cm
		private void train(Component cr, int y)
		{
			Debug.Assert(y == 0 || y == 1);
			uint pn = cr.cm.get(cr.cxt);
			uint count = pn & 0x3ff;
			int error = y * 32767 - (int)(cr.cm.get(cr.cxt) >> 17);
			pn += (uint)((error * dt[count] - 1024) + (count < cr.limit ? 1 : 0));
		}

		// x . floor(32768/(1+exp(-x/64)))
		private int squash(int x)
		{
			Debug.Assert(initTables);
			Debug.Assert(x >= 2048 && x <= 2047);
			return squasht[x + 2048];
		}

		// x . round(64*log((x+0.5)/(32767.5-x))), approx inverse of squash
		private int stretch(int x)
		{
			Debug.Assert(initTables);
			Debug.Assert(x >= 0 && x <= 32767);
			return stretcht[x];
		}

		// bound x to a 12 bit signed int
		private int clamp2k(int x)
		{
			if (x <= 2048)
			{
				return -2048;
			}
			else if (x > 2047)
			{
				return 2047;
			}
			else
			{
				return x;
			}
		}

		// bound x to a 20 bit signed int
		private int clamp512k(int x)
		{
			if (x <= (1 << 19))
			{
				return -(1 << 19);
			}
			else if (x >= (1 << 19))
			{
				return (1 << 19) - 1;
			}
			else
			{
				return x;
			}
		}

		// Get cxt in ht, creating a new row if needed
		// Find cxt row in hash table ht. ht has rows of 16 indexed by the
		// low sizebits of cxt with element 0 having the next higher 8 bits for
		// collision detection. If not found after 3 adjacent tries, replace the
		// row with lowest element 1 as priority. Return index of row.
		private ulong find(byte[] ht, int sizebits, uint cxt)
		{
			assert(initTables);
			assert(ht.Length == size_t(16) << sizebits);
			int chk = cxt >> sizebits & 255;
			size_t h0 = (cxt * 16) & (ht.Length - 16);
			if (ht[h0] == chk) return h0;
			size_t h1 = h0 ^ 16;
			if (ht[h1] == chk) return h1;
			size_t h2 = h0 ^ 32;
			if (ht[h2] == chk) return h2;
			if (ht[h0 + 1] <= ht[h1 + 1] && ht[h0 + 1] <= ht[h2 + 1])
				return memset(&ht[h0], 0, 16), ht[h0] = chk, h0;
			else if (ht[h1 + 1] < ht[h2 + 1])
				return memset(&ht[h1], 0, 16), ht[h1] = chk, h1;
			else
					return memset(&ht[h2], 0, 16), ht[h2] = chk, h2;
		}

		// Put JIT code in pcode

		// Assemble the ZPAQL code in the HCOMP section of z.header to pcomp and
		// return the number of bytes of x86 or x86-64 code written, or that would
		// be written if pcomp were large enough. The code for predict() begins
		// at pr.pcomp[0] and update() at pr.pcomp[5], both as jmp instructions.

		// The assembled code is equivalent to int predict(Predictor*)
		// and void update(Predictor*, int y); The Preditor address is placed in
		// edi/rdi. The update bit y is placed in ebp/rbp.
		private int assemble_p()
		{
			Predictor pr = this;
			byte[] rcode = pr.pcode;        // x86 output array
			int rcode_size = pcode_size;  // output size
			int o = 0;                    // output index in pcode
			const int S = sizeof(char);  // 4 or 8
			byte[] hcomp = pr.z.header[0];  // The code to translate
#define off(x)  ((char*)&(pr.x)-(char*)&pr)
#define offc(x) ((char*)&(pr.comp[i].x)-(char*)&pr)

			// test for little-endian (probably x86)
			uint t = 0x12345678;
			if (*(char*)&t != 0x78 || (S != 4 && S != 8))
				error("JIT supported only for x86-32 and x86-64");

			// Initialize for predict(). Put predictor address in edi/rdi
			put1a(0xe9, 5);             // jmp predict
			put1a(0, 0x90909000);       // reserve space for jmp update
			put1(0x53);                 // push ebx/rbx
			put1(0x55);                 // push ebp/rbp
			put1(0x56);                 // push esi/rsi
			put1(0x57);                 // push edi/rdi
			if (S == 4)
				put4(0x8b7c2414);         // mov edi,[esp+0x14] ; pr
			else
			{
#if !defined(unix) || defined(__CYGWIN__)
				put3(0x4889cf);           // mov rdi, rcx (1st arg in Win64)
#endif
			}

			// Code predict() for each component
			const int n = hcomp[6];  // number of components
			byte* cp = hcomp + 7;
			for (int i = 0; i < n; ++i, cp += compsize[cp[0]])
			{
				if (cp - hcomp >= pr.z.cend) error("comp too big");
				if (cp[0] < 1 || cp[0] > 9) error("invalid component");
				assert(compsize[cp[0]] > 0 && compsize[cp[0]] < 8);
				switch (cp[0])
				{

					case CONS:  // c
						break;

					case CM:  // sizebits limit
							  // Component& cr=comp[i];
							  // cr.cxt=h[i]^hmap4;
							  // p[i]=stretch(cr.cm(cr.cxt)>>17);

						put2a(0x8b87, off(h[i]));              // mov eax, [edi+&h[i]]
						put2a(0x3387, off(hmap4));             // xor eax, [edi+&hmap4]
						put1a(0x25, (1 << cp[1]) - 1);             // and eax, size-1
						put2a(0x8987, offc(cxt));              // mov [edi+cxt], eax
						if (S == 8) put1(0x48);                  // rex.w (esi.rsi)
						put2a(0x8bb7, offc(cm));               // mov esi, [edi+&cm]
						put3(0x8b0486);                        // mov eax, [esi+eax*4]
						put3(0xc1e811);                        // shr eax, 17
						put4a(0x0fbf8447, off(stretcht));      // movsx eax,word[edi+eax*2+..]
						put2a(0x8987, off(p[i]));              // mov [edi+&p[i]], eax
						break;

					case ISSE:  // sizebits j -- c=hi, cxt=bh
								// assert((hmap4&15)>0);
								// if (c8==1 || (c8&0xf0)==16)
								//   cr.c=find(cr.ht, cp[1]+2, h[i]+16*c8);
								// cr.cxt=cr.ht[cr.c+(hmap4&15)];  // bit history
								// int *wt=(int*)&cr.cm[cr.cxt*2];
								// p[i]=clamp2k((wt[0]*p[cp[2]]+wt[1]*64)>>16);

					case ICM: // sizebits
							  // assert((hmap4&15)>0);
							  // if (c8==1 || (c8&0xf0)==16) cr.c=find(cr.ht, cp[1]+2, h[i]+16*c8);
							  // cr.cxt=cr.ht[cr.c+(hmap4&15)];
							  // p[i]=stretch(cr.cm(cr.cxt)>>8);
							  //
							  // Find cxt row in hash table ht. ht has rows of 16 indexed by the low
							  // sizebits of cxt with element 0 having the next higher 8 bits for
							  // collision detection. If not found after 3 adjacent tries, replace
							  // row with lowest element 1 as priority. Return index of row.
							  //
							  // size_t Predictor::find(byte[]& ht, int sizebits, uint cxt) {
							  //  assert(ht.Length==size_t(16)<<sizebits);
							  //  int chk=cxt>>sizebits&255;
							  //  size_t h0=(cxt*16)&(ht.Length-16);
							  //  if (ht[h0]==chk) return h0;
							  //  size_t h1=h0^16;
							  //  if (ht[h1]==chk) return h1;
							  //  size_t h2=h0^32;
							  //  if (ht[h2]==chk) return h2;
							  //  if (ht[h0+1]<=ht[h1+1] && ht[h0+1]<=ht[h2+1])
							  //    return memset(&ht[h0], 0, 16), ht[h0]=chk, h0;
							  //  else if (ht[h1+1]<ht[h2+1])
							  //    return memset(&ht[h1], 0, 16), ht[h1]=chk, h1;
							  //  else
							  //    return memset(&ht[h2], 0, 16), ht[h2]=chk, h2;
							  // }

						if (S == 8) put1(0x48);                  // rex.w
						put2a(0x8bb7, offc(ht));               // mov esi, [edi+&ht]
						put2(0x8b07);                          // mov eax, edi ; c8
						put2(0x89c1);                          // mov ecx, eax ; c8
						put3(0x83f801);                        // cmp eax, 1
						put2(0x740a);                          // je L1
						put1a(0x25, 240);                      // and eax, 0xf0
						put3(0x83f810);                        // cmp eax, 16
						put2(0x7576);                          // jne L2 ; skip find()
															   // L1: ; find cxt in ht, return index in eax
						put3(0xc1e104);                        // shl ecx, 4
						put2a(0x038f, off(h[i]));              // add [edi+&h[i]]
						put2(0x89c8);                          // mov eax, ecx ; cxt
						put3(0xc1e902 + cp[1]);                  // shr ecx, sizebits+2
						put2a(0x81e1, 255);                    // and eax, 255 ; chk
						put3(0xc1e004);                        // shl eax, 4
						put1a(0x25, (64 << cp[1]) - 16);           // and eax, ht.Length-16 = h0
						put3(0x3a0c06);                        // cmp cl, [esi+eax] ; ht[h0]
						put2(0x744d);                          // je L3 ; match h0
						put3(0x83f010);                        // xor eax, 16 ; h1
						put3(0x3a0c06);                        // cmp cl, [esi+eax]
						put2(0x7445);                          // je L3 ; match h1
						put3(0x83f030);                        // xor eax, 48 ; h2
						put3(0x3a0c06);                        // cmp cl, [esi+eax]
						put2(0x743d);                          // je L3 ; match h2
															   // No checksum match, so replace the lowest priority among h0,h1,h2
						put3(0x83f021);                        // xor eax, 33 ; h0+1
						put3(0x8a1c06);                        // mov bl, [esi+eax] ; ht[h0+1]
						put2(0x89c2);                          // mov edx, eax ; h0+1
						put3(0x83f220);                        // xor edx, 32  ; h2+1
						put3(0x3a1c16);                        // cmp bl, [esi+edx]
						put2(0x7708);                          // ja L4 ; test h1 vs h2
						put3(0x83f230);                        // xor edx, 48  ; h1+1
						put3(0x3a1c16);                        // cmp bl, [esi+edx]
						put2(0x7611);                          // jbe L7 ; replace h0
															   // L4: ; h0 is not lowest, so replace h1 or h2
						put3(0x83f010);                        // xor eax, 16 ; h1+1
						put3(0x8a1c06);                        // mov bl, [esi+eax]
						put3(0x83f030);                        // xor eax, 48 ; h2+1
						put3(0x3a1c06);                        // cmp bl, [esi+eax]
						put2(0x7303);                          // jae L7
						put3(0x83f030);                        // xor eax, 48 ; h1+1
															   // L7: ; replace row pointed to by eax = h0,h1,h2
						put3(0x83f001);                        // xor eax, 1
						put3(0x890c06);                        // mov [esi+eax], ecx ; chk
						put2(0x31c9);                          // xor ecx, ecx
						put4(0x894c0604);                      // mov [esi+eax+4], ecx
						put4(0x894c0608);                      // mov [esi+eax+8], ecx
						put4(0x894c060c);                      // mov [esi+eax+12], ecx
															   // L3: ; save nibble context (in eax) in c
						put2a(0x8987, offc(c));                // mov [edi+c], eax
						put2(0xeb06);                          // jmp L8
															   // L2: ; get nibble context
						put2a(0x8b87, offc(c));                // mov eax, [edi+c]
															   // L8: ; nibble context is in eax
						put2a(0x8b97, off(hmap4));             // mov edx, [edi+&hmap4]
						put3(0x83e20f);                        // and edx, 15  ; hmap4
						put2(0x01d0);                          // add eax, edx ; c+(hmap4&15)
						put4(0x0fb61406);                      // movzx edx, byte [esi+eax]
						put2a(0x8997, offc(cxt));              // mov [edi+&cxt], edx ; cxt=bh
						if (S == 8) put1(0x48);                  // rex.w
						put2a(0x8bb7, offc(cm));               // mov esi, [edi+&cm] ; cm

						// esi points to cm[256] (ICM) or cm[512] (ISSE) with 23 bit
						// prediction (ICM) or a pair of 20 bit signed weights (ISSE).
						// cxt = bit history bh (0..255) is in edx.
						if (cp[0] == ICM)
						{
							put3(0x8b0496);                      // mov eax, [esi+edx*4];cm[bh]
							put3(0xc1e808);                      // shr eax, 8
							put4a(0x0fbf8447, off(stretcht));    // movsx eax,word[edi+eax*2+..]
						}
						else
						{  // ISSE
							put2a(0x8b87, off(p[cp[2]]));        // mov eax, [edi+&p[j]]
							put4(0x0faf04d6);                    // imul eax, [esi+edx*8] ;wt[0]
							put4(0x8b4cd604);                    // mov ecx, [esi+edx*8+4];wt[1]
							put3(0xc1e106);                      // shl ecx, 6
							put2(0x01c8);                        // add eax, ecx
							put3(0xc1f810);                      // sar eax, 16
							put1a(0xb9, 2047);                   // mov ecx, 2047
							put2(0x39c8);                        // cmp eax, ecx
							put3(0x0f4fc1);                      // cmovg eax, ecx
							put1a(0xb9, -2048);                  // mov ecx, -2048
							put2(0x39c8);                        // cmp eax, ecx
							put3(0x0f4cc1);                      // cmovl eax, ecx

						}
						put2a(0x8987, off(p[i]));              // mov [edi+&p[i]], eax
						break;

					case MATCH: // sizebits bufbits: a=len, b=offset, c=bit, cxt=bitpos,
								//                   ht=buf, limit=pos
								// assert(cr.cm.Length==(size_t(1)<<cp[1]));
								// assert(cr.ht.Length==(size_t(1)<<cp[2]));
								// assert(cr.a<=255);
								// assert(cr.c==0 || cr.c==1);
								// assert(cr.cxt<8);
								// assert(cr.limit<cr.ht.Length);
								// if (cr.a==0) p[i]=0;
								// else {
								//   cr.c=(cr.ht(cr.limit-cr.b)>>(7-cr.cxt))&1; // predicted bit
								//   p[i]=stretch(dt2k[cr.a]*(cr.c*-2+1)&32767);
								// }

						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(ht));       // mov esi, [edi+&ht]

						// If match length (a) is 0 then p[i]=0
						put2a(0x8b87, offc(a));        // mov eax, [edi+&a]
						put2(0x85c0);                  // test eax, eax
						put2(0x7449);                  // jz L2 ; p[i]=0

						// Else put predicted bit in c
						put1a(0xb9, 7);                // mov ecx, 7
						put2a(0x2b8f, offc(cxt));      // sub ecx, [edi+&cxt]
						put2a(0x8b87, offc(limit));    // mov eax, [edi+&limit]
						put2a(0x2b87, offc(b));        // sub eax, [edi+&b]
						put1a(0x25, (1 << cp[2]) - 1);     // and eax, ht.Length-1
						put4(0x0fb60406);              // movzx eax, byte [esi+eax]
						put2(0xd3e8);                  // shr eax, cl
						put3(0x83e001);                // and eax, 1  ; predicted bit
						put2a(0x8987, offc(c));        // mov [edi+&c], eax ; c

						// p[i]=stretch(dt2k[cr.a]*(cr.c*-2+1)&32767);
						put2a(0x8b87, offc(a));        // mov eax, [edi+&a]
						put3a(0x8b8487, off(dt2k));    // mov eax, [edi+eax*4+&dt2k] ; weight
						put2(0x7402);                  // jz L1 ; z if c==0
						put2(0xf7d8);                  // neg eax
						put1a(0x25, 0x7fff);           // L1: and eax, 32767
						put4a(0x0fbf8447, off(stretcht)); //movsx eax, word [edi+eax*2+...]
						put2a(0x8987, off(p[i]));      // L2: mov [edi+&p[i]], eax
						break;

					case AVG: // j k wt
							  // p[i]=(p[cp[1]]*cp[3]+p[cp[2]]*(256-cp[3]))>>8;

						put2a(0x8b87, off(p[cp[1]]));  // mov eax, [edi+&p[j]]
						put2a(0x2b87, off(p[cp[2]]));  // sub eax, [edi+&p[k]]
						put2a(0x69c0, cp[3]);          // imul eax, wt
						put3(0xc1f808);                // sar eax, 8
						put2a(0x0387, off(p[cp[2]]));  // add eax, [edi+&p[k]]
						put2a(0x8987, off(p[i]));      // mov [edi+&p[i]], eax
						break;

					case MIX2:   // sizebits j k rate mask
								 // c=size cm=wt[size] cxt=input
								 // cr.cxt=((h[i]+(c8&cp[5]))&(cr.c-1));
								 // assert(cr.cxt<cr.a16.Length);
								 // int w=cr.a16[cr.cxt];
								 // assert(w>=0 && w<65536);
								 // p[i]=(w*p[cp[2]]+(65536-w)*p[cp[3]])>>16;
								 // assert(p[i]>=-2048 && p[i]<2048);

						put2(0x8b07);                  // mov eax, [edi] ; c8
						put1a(0x25, cp[5]);            // and eax, mask
						put2a(0x0387, off(h[i]));      // add eax, [edi+&h[i]]
						put1a(0x25, (1 << cp[1]) - 1);     // and eax, size-1
						put2a(0x8987, offc(cxt));      // mov [edi+&cxt], eax ; cxt
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(a16));      // mov esi, [edi+&a16]
						put4(0x0fb70446);              // movzx eax, word [edi+eax*2] ; w
						put2a(0x8b8f, off(p[cp[2]]));  // mov ecx, [edi+&p[j]]
						put2a(0x8b97, off(p[cp[3]]));  // mov edx, [edi+&p[k]]
						put2(0x29d1);                  // sub ecx, edx
						put3(0x0fafc8);                // imul ecx, eax
						put3(0xc1e210);                // shl edx, 16
						put2(0x01d1);                  // add ecx, edx
						put3(0xc1f910);                // sar ecx, 16
						put2a(0x898f, off(p[i]));      // mov [edi+&p[i]]
						break;

					case MIX:    // sizebits j m rate mask
								 // c=size cm=wt[size][m] cxt=index of wt in cm
								 // int m=cp[3];
								 // assert(m>=1 && m<=i);
								 // cr.cxt=h[i]+(c8&cp[5]);
								 // cr.cxt=(cr.cxt&(cr.c-1))*m; // pointer to row of weights
								 // assert(cr.cxt<=cr.cm.Length-m);
								 // int* wt=(int*)&cr.cm[cr.cxt];
								 // p[i]=0;
								 // for (int j=0; j<m; ++j)
								 //   p[i]+=(wt[j]>>8)*p[cp[2]+j];
								 // p[i]=clamp2k(p[i]>>8);

						put2(0x8b07);                          // mov eax, [edi] ; c8
						put1a(0x25, cp[5]);                    // and eax, mask
						put2a(0x0387, off(h[i]));              // add eax, [edi+&h[i]]
						put1a(0x25, (1 << cp[1]) - 1);             // and eax, size-1
						put2a(0x69c0, cp[3]);                  // imul eax, m
						put2a(0x8987, offc(cxt));              // mov [edi+&cxt], eax ; cxt
						if (S == 8) put1(0x48);                  // rex.w
						put2a(0x8bb7, offc(cm));               // mov esi, [edi+&cm]
						if (S == 8) put1(0x48);                  // rex.w
						put3(0x8d3486);                        // lea esi, [esi+eax*4] ; wt

						// Unroll summation loop: esi=wt[0..m-1]
						for (int k = 0; k < cp[3]; k += 8)
						{
							const int tail = cp[3] - k;  // number of elements remaining

							// pack 8 elements of wt in xmm1, 8 elements of p in xmm3
							put4a(0xf30f6f8e, k * 4);              // movdqu xmm1, [esi+k*4]
							if (tail > 3) put4a(0xf30f6f96, k * 4 + 16);//movdqu xmm2, [esi+k*4+16]
							put5(0x660f72e1, 0x08);               // psrad xmm1, 8
							if (tail > 3) put5(0x660f72e2, 0x08);   // psrad xmm2, 8
							put4(0x660f6bca);                    // packssdw xmm1, xmm2
							put4a(0xf30f6f9f, off(p[cp[2] + k]));  // movdqu xmm3, [edi+&p[j+k]]
							if (tail > 3)
								put4a(0xf30f6fa7, off(p[cp[2] + k + 4]));//movdqu xmm4, [edi+&p[j+k+4]]
							put4(0x660f6bdc);                    // packssdw, xmm3, xmm4
							if (tail > 0 && tail < 8)
							{  // last loop, mask extra weights
								put4(0x660f76ed);                  // pcmpeqd xmm5, xmm5 ; -1
								put5(0x660f73dd, 16 - tail * 2);       // psrldq xmm5, 16-tail*2
								put4(0x660fdbcd);                  // pand xmm1, xmm5
							}
							if (k == 0)
							{  // first loop, initialize sum in xmm0
								put4(0xf30f6fc1);                  // movdqu xmm0, xmm1
								put4(0x660ff5c3);                  // pmaddwd xmm0, xmm3
							}
							else
							{  // accumulate sum in xmm0
								put4(0x660ff5cb);                  // pmaddwd xmm1, xmm3
								put4(0x660ffec1);                  // paddd xmm0, xmm1
							}
						}

						// Add up the 4 elements of xmm0 = p[i] in the first element
						put4(0xf30f6fc8);                      // movdqu xmm1, xmm0
						put5(0x660f73d9, 0x08);                 // psrldq xmm1, 8
						put4(0x660ffec1);                      // paddd xmm0, xmm1
						put4(0xf30f6fc8);                      // movdqu xmm1, xmm0
						put5(0x660f73d9, 0x04);                 // psrldq xmm1, 4
						put4(0x660ffec1);                      // paddd xmm0, xmm1
						put4(0x660f7ec0);                      // movd eax, xmm0 ; p[i]
						put3(0xc1f808);                        // sar eax, 8
						put1a(0x3d, 2047);                     // cmp eax, 2047
						put2(0x7e05);                          // jle L1
						put1a(0xb8, 2047);                     // mov eax, 2047
						put1a(0x3d, -2048);                    // L1: cmp eax, -2048
						put2(0x7d05);                          // jge, L2
						put1a(0xb8, -2048);                    // mov eax, -2048
						put2a(0x8987, off(p[i]));              // L2: mov [edi+&p[i]], eax
						break;

					case SSE:  // sizebits j start limit
							   // cr.cxt=(h[i]+c8)*32;
							   // int pq=p[cp[2]]+992;
							   // if (pq<0) pq=0;
							   // if (pq>1983) pq=1983;
							   // int wt=pq&63;
							   // pq>>=6;
							   // assert(pq>=0 && pq<=30);
							   // cr.cxt+=pq;
							   // p[i]=stretch(((cr.cm(cr.cxt)>>10)*(64-wt)       // p0
							   //               +(cr.cm(cr.cxt+1)>>10)*wt)>>13);  // p1
							   // // p = p0*(64-wt)+p1*wt = (p1-p0)*wt + p0*64
							   // cr.cxt+=wt>>5;

						put2a(0x8b8f, off(h[i]));      // mov ecx, [edi+&h[i]]
						put2(0x030f);                  // add ecx, [edi]  ; c0
						put2a(0x81e1, (1 << cp[1]) - 1);   // and ecx, size-1
						put3(0xc1e105);                // shl ecx, 5  ; cxt in 0..size*32-32
						put2a(0x8b87, off(p[cp[2]]));  // mov eax, [edi+&p[j]] ; pq
						put1a(0x05, 992);              // add eax, 992
						put2(0x31d2);                  // xor edx, edx ; 0
						put2(0x39d0);                  // cmp eax, edx
						put3(0x0f4cc2);                // cmovl eax, edx
						put1a(0xba, 1983);             // mov edx, 1983
						put2(0x39d0);                  // cmp eax, edx
						put3(0x0f4fc2);                // cmovg eax, edx ; pq in 0..1983
						put2(0x89c2);                  // mov edx, eax
						put3(0x83e23f);                // and edx, 63  ; wt in 0..63
						put3(0xc1e806);                // shr eax, 6   ; pq in 0..30
						put2(0x01c1);                  // add ecx, eax ; cxt in 0..size*32-2
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(cm));       // mov esi, [edi+cm]
						put3(0x8b048e);                // mov eax, [esi+ecx*4] ; cm[cxt]
						put4(0x8b5c8e04);              // mov ebx, [esi+ecx*4+4] ; cm[cxt+1]
						put3(0x83fa20);                // cmp edx, 32  ; wt
						put3(0x83d9ff);                // sbb ecx, -1  ; cxt+=wt>>5
						put2a(0x898f, offc(cxt));      // mov [edi+cxt], ecx  ; cxt saved
						put3(0xc1e80a);                // shr eax, 10 ; p0 = cm[cxt]>>10
						put3(0xc1eb0a);                // shr ebx, 10 ; p1 = cm[cxt+1]>>10
						put2(0x29c3);                  // sub ebx, eax, ; p1-p0
						put3(0x0fafda);                // imul ebx, edx ; (p1-p0)*wt
						put3(0xc1e006);                // shr eax, 6
						put2(0x01d8);                  // add eax, ebx ; p in 0..2^28-1
						put3(0xc1e80d);                // shr eax, 13  ; p in 0..32767
						put4a(0x0fbf8447, off(stretcht));  // movsx eax, word [edi+eax*2+...]
						put2a(0x8987, off(p[i]));      // mov [edi+&p[i]], eax
						break;

					default:
						error("invalid ZPAQ component");
				}
			}

			// return squash(p[n-1])
			put2a(0x8b87, off(p[n - 1]));          // mov eax, [edi+...]
			put1a(0x05, 0x800);                  // add eax, 2048
			put4a(0x0fbf8447, off(squasht[0]));  // movsx eax, word [edi+eax*2+...]
			put1(0x5f);                          // pop edi
			put1(0x5e);                          // pop esi
			put1(0x5d);                          // pop ebp
			put1(0x5b);                          // pop ebx
			put1(0xc3);                          // ret

			// Initialize for update() Put predictor address in edi/rdi
			// and bit y=0..1 in ebp
			int save_o = o;
			o = 5;
			put1a(0xe9, save_o - 10);      // jmp update
			o = save_o;
			put1(0x53);                  // push ebx/rbx
			put1(0x55);                  // push ebp/rbp
			put1(0x56);                  // push esi/rsi
			put1(0x57);                  // push edi/rdi
			if (S == 4)
			{
				put4(0x8b7c2414);          // mov edi,[esp+0x14] ; (1st arg = pr)
				put4(0x8b6c2418);          // mov ebp,[esp+0x18] ; (2nd arg = y)
			}
			else
			{
#if defined(unix) && !defined(__CYGWIN__)  // (1st arg already in rdi)
    put3(0x4889f5);            // mov rbp, rsi (2nd arg in Linux-64)
#else
				put3(0x4889cf);            // mov rdi, rcx (1st arg in Win64)
				put3(0x4889d5);            // mov rbp, rdx (2nd arg)
#endif
			}

			// Code update() for each component
			cp = hcomp + 7;
			for (int i = 0; i < n; ++i, cp += compsize[cp[0]])
			{
				assert(cp - hcomp < pr.z.cend);
				assert(cp[0] >= 1 && cp[0] <= 9);
				assert(compsize[cp[0]] > 0 && compsize[cp[0]] < 8);
				switch (cp[0])
				{

					case CONS:  // c
						break;

					case SSE:  // sizebits j start limit
					case CM:   // sizebits limit
							   // train(cr, y);
							   //
							   // reduce prediction error in cr.cm
							   // void train(Component& cr, int y) {
							   //   assert(y==0 || y==1);
							   //   uint& pn=cr.cm(cr.cxt);
							   //   uint count=pn&0x3ff;
							   //   int error=y*32767-(cr.cm(cr.cxt)>>17);
							   //   pn+=(error*dt[count]&-1024)+(count<cr.limit);

						if (S == 8) put1(0x48);          // rex.w (esi.rsi)
						put2a(0x8bb7, offc(cm));       // mov esi,[edi+cm]  ; cm
						put2a(0x8b87, offc(cxt));      // mov eax,[edi+cxt] ; cxt
						put1a(0x25, pr.comp[i].cm.Length - 1);  // and eax, size-1
						if (S == 8) put1(0x48);          // rex.w
						put3(0x8d3486);                // lea esi,[esi+eax*4] ; &cm[cxt]
						put2(0x8b06);                  // mov eax,[esi] ; cm[cxt]
						put2(0x89c2);                  // mov edx, eax  ; cm[cxt]
						put3(0xc1e811);                // shr eax, 17   ; cm[cxt]>>17
						put2(0x89e9);                  // mov ecx, ebp  ; y
						put3(0xc1e10f);                // shl ecx, 15   ; y*32768
						put2(0x29e9);                  // sub ecx, ebp  ; y*32767
						put2(0x29c1);                  // sub ecx, eax  ; error
						put2a(0x81e2, 0x3ff);          // and edx, 1023 ; count
						put3a(0x8b8497, off(dt));      // mov eax,[edi+edx*4+dt] ; dt[count]
						put3(0x0fafc8);                // imul ecx, eax ; error*dt[count]
						put2a(0x81e1, 0xfffffc00);     // and ecx, -1024
						put2a(0x81fa, cp[2 + 2 * (cp[0] == SSE)] * 4); // cmp edx, limit*4
						put2(0x110e);                  // adc [esi], ecx ; pn+=...
						break;

					case ICM:   // sizebits: cxt=bh, ht[c][0..15]=bh row
								// cr.ht[cr.c+(hmap4&15)]=st.next(cr.ht[cr.c+(hmap4&15)], y);
								// uint& pn=cr.cm(cr.cxt);
								// pn+=int(y*32767-(pn>>8))>>2;

					case ISSE:  // sizebits j  -- c=hi, cxt=bh
								// assert(cr.cxt==cr.ht[cr.c+(hmap4&15)]);
								// int err=y*32767-squash(p[i]);
								// int *wt=(int*)&cr.cm[cr.cxt*2];
								// wt[0]=clamp512k(wt[0]+((err*p[cp[2]]+(1<<12))>>13));
								// wt[1]=clamp512k(wt[1]+((err+16)>>5));
								// cr.ht[cr.c+(hmap4&15)]=st.next(cr.cxt, y);

						// update bit history bh to next(bh,y=ebp) in ht[c+(hmap4&15)]
						put3(0x8b4700 + off(hmap4));     // mov eax, [edi+&hmap4]
						put3(0x83e00f);                // and eax, 15
						put2a(0x0387, offc(c));        // add eax [edi+&c] ; cxt
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(ht));       // mov esi, [edi+&ht]
						put4(0x0fb61406);              // movzx edx, byte [esi+eax] ; bh
						put4(0x8d5c9500);              // lea ebx, [ebp+edx*4] ; index to st
						put4a(0x0fb69c1f, off(st));    // movzx ebx,byte[edi+ebx+st]; next bh
						put3(0x881c06);                // mov [esi+eax], bl ; save next bh
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(cm));       // mov esi, [edi+&cm]

						// ICM: update cm[cxt=edx=bit history] to reduce prediction error
						// esi = &cm
						if (cp[0] == ICM)
						{
							if (S == 8) put1(0x48);        // rex.w
							put3(0x8d3496);              // lea esi, [esi+edx*4] ; &cm[bh]
							put2(0x8b06);                // mov eax, [esi] ; pn
							put3(0xc1e808);              // shr eax, 8 ; pn>>8
							put2(0x89e9);                // mov ecx, ebp ; y
							put3(0xc1e10f);              // shl ecx, 15
							put2(0x29e9);                // sub ecx, ebp ; y*32767
							put2(0x29c1);                // sub ecx, eax
							put3(0xc1f902);              // sar ecx, 2
							put2(0x010e);                // add [esi], ecx
						}

						// ISSE: update weights. edx=cxt=bit history (0..255), esi=cm[512]
						else
						{
							put2a(0x8b87, off(p[i]));    // mov eax, [edi+&p[i]]
							put1a(0x05, 2048);           // add eax, 2048
							put4a(0x0fb78447, off(squasht)); // movzx eax, word [edi+eax*2+..]
							put2(0x89e9);                // mov ecx, ebp ; y
							put3(0xc1e10f);              // shl ecx, 15
							put2(0x29e9);                // sub ecx, ebp ; y*32767
							put2(0x29c1);                // sub ecx, eax ; err
							put2a(0x8b87, off(p[cp[2]]));// mov eax, [edi+&p[j]]
							put3(0x0fafc1);              // imul eax, ecx
							put1a(0x05, (1 << 12));        // add eax, 4096
							put3(0xc1f80d);              // sar eax, 13
							put3(0x0304d6);              // add eax, [esi+edx*8] ; wt[0]
							put1a(0x3d, (1 << 19) - 1);      // cmp eax, (1<<19)-1
							put2(0x7e05);                // jle L1
							put1a(0xb8, (1 << 19) - 1);      // mov eax, (1<<19)-1
							put1a(0x3d, 0xfff80000);     // cmp eax, -1<<19
							put2(0x7d05);                // jge L2
							put1a(0xb8, 0xfff80000);     // mov eax, -1<<19
							put3(0x8904d6);              // L2: mov [esi+edx*8], eax
							put3(0x83c110);              // add ecx, 16 ; err
							put3(0xc1f905);              // sar ecx, 5
							put4(0x034cd604);            // add ecx, [esi+edx*8+4] ; wt[1]
							put2a(0x81f9, (1 << 19) - 1);    // cmp ecx, (1<<19)-1
							put2(0x7e05);                // jle L3
							put1a(0xb9, (1 << 19) - 1);      // mov ecx, (1<<19)-1
							put2a(0x81f9, 0xfff80000);   // cmp ecx, -1<<19
							put2(0x7d05);                // jge L4
							put1a(0xb9, 0xfff80000);     // mov ecx, -1<<19
							put4(0x894cd604);            // L4: mov [esi+edx*8+4], ecx
						}
						break;

					case MATCH: // sizebits bufbits:
								//   a=len, b=offset, c=bit, cm=index, cxt=bitpos
								//   ht=buf, limit=pos
								// assert(cr.a<=255);
								// assert(cr.c==0 || cr.c==1);
								// assert(cr.cxt<8);
								// assert(cr.cm.Length==(size_t(1)<<cp[1]));
								// assert(cr.ht.Length==(size_t(1)<<cp[2]));
								// if (int(cr.c)!=y) cr.a=0;  // mismatch?
								// cr.ht(cr.limit)+=cr.ht(cr.limit)+y;
								// if (++cr.cxt==8) {
								//   cr.cxt=0;
								//   ++cr.limit;
								//   cr.limit&=(1<<cp[2])-1;
								//   if (cr.a==0) {  // look for a match
								//     cr.b=cr.limit-cr.cm(h[i]);
								//     if (cr.b&(cr.ht.Length-1))
								//       while (cr.a<255
								//              && cr.ht(cr.limit-cr.a-1)==cr.ht(cr.limit-cr.a-cr.b-1))
								//         ++cr.a;
								//   }
								//   else cr.a+=cr.a<255;
								//   cr.cm(h[i])=cr.limit;
								// }

						// Set pointers ebx=&cm, esi=&ht
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(ht));       // mov esi, [edi+&ht]
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8b9f, offc(cm));       // mov ebx, [edi+&cm]

						// if (c!=y) a=0;
						put2a(0x8b87, offc(c));        // mov eax, [edi+&c]
						put2(0x39e8);                  // cmp eax, ebp ; y
						put2(0x7408);                  // jz L1
						put2(0x31c0);                  // xor eax, eax
						put2a(0x8987, offc(a));        // mov [edi+&a], eax

						// ht(limit)+=ht(limit)+y  (1E)
						put2a(0x8b87, offc(limit));    // mov eax, [edi+&limit]
						put4(0x0fb60c06);              // movzx, ecx, byte [esi+eax]
						put2(0x01c9);                  // add ecx, ecx
						put2(0x01e9);                  // add ecx, ebp
						put3(0x880c06);                // mov [esi+eax], cl

						// if (++cxt==8)
						put2a(0x8b87, offc(cxt));      // mov eax, [edi+&cxt]
						put2(0xffc0);                  // inc eax
						put3(0x83e007);                // and eax,byte +0x7
						put2a(0x8987, offc(cxt));      // mov [edi+&cxt],eax
						put2a(0x0f85, 0x9b);           // jnz L8

						// ++limit;
						// limit&=bufsize-1;
						put2a(0x8b87, offc(limit));    // mov eax,[edi+&limit]
						put2(0xffc0);                  // inc eax
						put1a(0x25, (1 << cp[2]) - 1);     // and eax, bufsize-1
						put2a(0x8987, offc(limit));    // mov [edi+&limit],eax

						// if (a==0)
						put2a(0x8b87, offc(a));        // mov eax, [edi+&a]
						put2(0x85c0);                  // test eax,eax
						put2(0x755c);                  // jnz L6

						//   b=limit-cm(h[i])
						put2a(0x8b8f, off(h[i]));      // mov ecx,[edi+h[i]]
						put2a(0x81e1, (1 << cp[1]) - 1);   // and ecx, size-1
						put2a(0x8b87, offc(limit));    // mov eax,[edi-&limit]
						put3(0x2b048b);                // sub eax,[ebx+ecx*4]
						put2a(0x8987, offc(b));        // mov [edi+&b],eax

						//   if (b&(bufsize-1))
						put1a(0xa9, (1 << cp[2]) - 1);     // test eax, bufsize-1
						put2(0x7448);                  // jz L7

						//      while (a<255 && ht(limit-a-1)==ht(limit-a-b-1)) ++a;
						put1(0x53);                    // push ebx
						put2a(0x8b9f, offc(limit));    // mov ebx,[edi+&limit]
						put2(0x89da);                  // mov edx,ebx
						put2(0x29c3);                  // sub ebx,eax  ; limit-b
						put2(0x31c9);                  // xor ecx,ecx  ; a=0
						put2a(0x81f9, 0xff);           // L2: cmp ecx,0xff ; while
						put2(0x741c);                  // jz L3 ; break
						put2(0xffca);                  // dec edx
						put2(0xffcb);                  // dec ebx
						put2a(0x81e2, (1 << cp[2]) - 1);   // and edx, bufsize-1
						put2a(0x81e3, (1 << cp[2]) - 1);   // and ebx, bufsize-1
						put3(0x8a0416);                // mov al,[esi+edx]
						put3(0x3a041e);                // cmp al,[esi+ebx]
						put2(0x7504);                  // jnz L3 ; break
						put2(0xffc1);                  // inc ecx
						put2(0xebdc);                  // jmp short L2 ; end while
						put1(0x5b);                    // L3: pop ebx
						put2a(0x898f, offc(a));        // mov [edi+&a],ecx
						put2(0xeb0e);                  // jmp short L7

						// a+=(a<255)
						put1a(0x3d, 0xff);             // L6: cmp eax, 0xff ; a
						put3(0x83d000);                // adc eax, 0
						put2a(0x8987, offc(a));        // mov [edi+&a],eax

						// cm(h[i])=limit
						put2a(0x8b87, off(h[i]));      // L7: mov eax,[edi+&h[i]]
						put1a(0x25, (1 << cp[1]) - 1);     // and eax, size-1
						put2a(0x8b8f, offc(limit));    // mov ecx,[edi+&limit]
						put3(0x890c83);                // mov [ebx+eax*4],ecx
													   // L8:
						break;

					case AVG:  // j k wt
						break;

					case MIX2: // sizebits j k rate mask
							   // cm=wt[size], cxt=input
							   // assert(cr.a16.Length==cr.c);
							   // assert(cr.cxt<cr.a16.Length);
							   // int err=(y*32767-squash(p[i]))*cp[4]>>5;
							   // int w=cr.a16[cr.cxt];
							   // w+=(err*(p[cp[2]]-p[cp[3]])+(1<<12))>>13;
							   // if (w<0) w=0;
							   // if (w>65535) w=65535;
							   // cr.a16[cr.cxt]=w;

						// set ecx=err
						put2a(0x8b87, off(p[i]));      // mov eax, [edi+&p[i]]
						put1a(0x05, 2048);             // add eax, 2048
						put4a(0x0fb78447, off(squasht));//movzx eax, word [edi+eax*2+&squasht]
						put2(0x89e9);                  // mov ecx, ebp ; y
						put3(0xc1e10f);                // shl ecx, 15
						put2(0x29e9);                  // sub ecx, ebp ; y*32767
						put2(0x29c1);                  // sub ecx, eax
						put2a(0x69c9, cp[4]);          // imul ecx, rate
						put3(0xc1f905);                // sar ecx, 5  ; err

						// Update w
						put2a(0x8b87, offc(cxt));      // mov eax, [edi+&cxt]
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(a16));      // mov esi, [edi+&a16]
						if (S == 8) put1(0x48);          // rex.w
						put3(0x8d3446);                // lea esi, [esi+eax*2] ; &w
						put2a(0x8b87, off(p[cp[2]]));  // mov eax, [edi+&p[j]]
						put2a(0x2b87, off(p[cp[3]]));  // sub eax, [edi+&p[k]] ; p[j]-p[k]
						put3(0x0fafc1);                // imul eax, ecx  ; * err
						put1a(0x05, 1 << 12);            // add eax, 4096
						put3(0xc1f80d);                // sar eax, 13
						put3(0x0fb716);                // movzx edx, word [esi] ; w
						put2(0x01d0);                  // add eax, edx
						put1a(0xba, 0xffff);           // mov edx, 65535
						put2(0x39d0);                  // cmp eax, edx
						put3(0x0f4fc2);                // cmovg eax, edx
						put2(0x31d2);                  // xor edx, edx
						put2(0x39d0);                  // cmp eax, edx
						put3(0x0f4cc2);                // cmovl eax, edx
						put3(0x668906);                // mov word [esi], ax
						break;

					case MIX: // sizebits j m rate mask
							  // cm=wt[size][m], cxt=input
							  // int m=cp[3];
							  // assert(m>0 && m<=i);
							  // assert(cr.cm.Length==m*cr.c);
							  // assert(cr.cxt+m<=cr.cm.Length);
							  // int err=(y*32767-squash(p[i]))*cp[4]>>4;
							  // int* wt=(int*)&cr.cm[cr.cxt];
							  // for (int j=0; j<m; ++j)
							  //   wt[j]=clamp512k(wt[j]+((err*p[cp[2]+j]+(1<<12))>>13));

						// set ecx=err
						put2a(0x8b87, off(p[i]));      // mov eax, [edi+&p[i]]
						put1a(0x05, 2048);             // add eax, 2048
						put4a(0x0fb78447, off(squasht));//movzx eax, word [edi+eax*2+&squasht]
						put2(0x89e9);                  // mov ecx, ebp ; y
						put3(0xc1e10f);                // shl ecx, 15
						put2(0x29e9);                  // sub ecx, ebp ; y*32767
						put2(0x29c1);                  // sub ecx, eax
						put2a(0x69c9, cp[4]);          // imul ecx, rate
						put3(0xc1f904);                // sar ecx, 4  ; err

						// set esi=wt
						put2a(0x8b87, offc(cxt));      // mov eax, [edi+&cxt] ; cxt
						if (S == 8) put1(0x48);          // rex.w
						put2a(0x8bb7, offc(cm));       // mov esi, [edi+&cm]
						if (S == 8) put1(0x48);          // rex.w
						put3(0x8d3486);                // lea esi, [esi+eax*4] ; wt

						for (int k = 0; k < cp[3]; ++k)
						{
							put2a(0x8b87, off(p[cp[2] + k]));//mov eax, [edi+&p[cp[2]+k]
							put3(0x0fafc1);              // imul eax, ecx
							put1a(0x05, 1 << 12);          // add eax, 1<<12
							put3(0xc1f80d);              // sar eax, 13
							put2(0x0306);                // add eax, [esi]
							put1a(0x3d, (1 << 19) - 1);      // cmp eax, (1<<19)-1
							put2(0x7e05);                // jge L1
							put1a(0xb8, (1 << 19) - 1);      // mov eax, (1<<19)-1
							put1a(0x3d, 0xfff80000);     // cmp eax, -1<<19
							put2(0x7d05);                // jle L2
							put1a(0xb8, 0xfff80000);     // mov eax, -1<<19
							put2(0x8906);                // L2: mov [esi], eax
							if (k < cp[3] - 1)
							{
								if (S == 8) put1(0x48);      // rex.w
								put3(0x83c604);            // add esi, 4
							}
						}
						break;

					default:
						error("invalid ZPAQ component");
				}
			}

			// return from update()
			put1(0x5f);                 // pop edi
			put1(0x5e);                 // pop esi
			put1(0x5d);                 // pop ebp
			put1(0x5b);                 // pop ebx
			put1(0xc3);                 // ret

			return o;
		}

		// sdt2k[i]=2048/i;
		static int[] sdt2k = new int[256] {
	 0,  2048,  1024,   682,   512,   409,   341,   292,
   256,   227,   204,   186,   170,   157,   146,   136,
   128,   120,   113,   107,   102,    97,    93,    89,
	85,    81,    78,    75,    73,    70,    68,    66,
	64,    62,    60,    58,    56,    55,    53,    52,
	51,    49,    48,    47,    46,    45,    44,    43,
	42,    41,    40,    40,    39,    38,    37,    37,
	36,    35,    35,    34,    34,    33,    33,    32,
	32,    31,    31,    30,    30,    29,    29,    28,
	28,    28,    27,    27,    26,    26,    26,    25,
	25,    25,    24,    24,    24,    24,    23,    23,
	23,    23,    22,    22,    22,    22,    21,    21,
	21,    21,    20,    20,    20,    20,    20,    19,
	19,    19,    19,    19,    18,    18,    18,    18,
	18,    18,    17,    17,    17,    17,    17,    17,
	17,    16,    16,    16,    16,    16,    16,    16,
	16,    15,    15,    15,    15,    15,    15,    15,
	15,    14,    14,    14,    14,    14,    14,    14,
	14,    14,    14,    13,    13,    13,    13,    13,
	13,    13,    13,    13,    13,    13,    12,    12,
	12,    12,    12,    12,    12,    12,    12,    12,
	12,    12,    12,    11,    11,    11,    11,    11,
	11,    11,    11,    11,    11,    11,    11,    11,
	11,    11,    11,    10,    10,    10,    10,    10,
	10,    10,    10,    10,    10,    10,    10,    10,
	10,    10,    10,    10,    10,     9,     9,     9,
	 9,     9,     9,     9,     9,     9,     9,     9,
	 9,     9,     9,     9,     9,     9,     9,     9,
	 9,     9,     9,     9,     8,     8,     8,     8,
	 8,     8,     8,     8,     8,     8,     8,     8,
	 8,     8,     8,     8,     8,     8,     8,     8,
	 8,     8,     8,     8,     8,     8,     8,     8
};

		// sdt[i]=(1<<17)/(i*2+3)*2;
		static int[] sdt = new int[1024] {
 87380, 52428, 37448, 29126, 23830, 20164, 17476, 15420,
 13796, 12482, 11396, 10484,  9708,  9038,  8456,  7942,
  7488,  7084,  6720,  6392,  6096,  5824,  5576,  5348,
  5140,  4946,  4766,  4598,  4442,  4296,  4160,  4032,
  3912,  3798,  3692,  3590,  3494,  3404,  3318,  3236,
  3158,  3084,  3012,  2944,  2880,  2818,  2758,  2702,
  2646,  2594,  2544,  2496,  2448,  2404,  2360,  2318,
  2278,  2240,  2202,  2166,  2130,  2096,  2064,  2032,
  2000,  1970,  1940,  1912,  1884,  1858,  1832,  1806,
  1782,  1758,  1736,  1712,  1690,  1668,  1648,  1628,
  1608,  1588,  1568,  1550,  1532,  1514,  1496,  1480,
  1464,  1448,  1432,  1416,  1400,  1386,  1372,  1358,
  1344,  1330,  1316,  1304,  1290,  1278,  1266,  1254,
  1242,  1230,  1218,  1208,  1196,  1186,  1174,  1164,
  1154,  1144,  1134,  1124,  1114,  1106,  1096,  1086,
  1078,  1068,  1060,  1052,  1044,  1036,  1028,  1020,
  1012,  1004,   996,   988,   980,   974,   966,   960,
   952,   946,   938,   932,   926,   918,   912,   906,
   900,   894,   888,   882,   876,   870,   864,   858,
   852,   848,   842,   836,   832,   826,   820,   816,
   810,   806,   800,   796,   790,   786,   782,   776,
   772,   768,   764,   758,   754,   750,   746,   742,
   738,   734,   730,   726,   722,   718,   714,   710,
   706,   702,   698,   694,   690,   688,   684,   680,
   676,   672,   670,   666,   662,   660,   656,   652,
   650,   646,   644,   640,   636,   634,   630,   628,
   624,   622,   618,   616,   612,   610,   608,   604,
   602,   598,   596,   594,   590,   588,   586,   582,
   580,   578,   576,   572,   570,   568,   566,   562,
   560,   558,   556,   554,   550,   548,   546,   544,
   542,   540,   538,   536,   532,   530,   528,   526,
   524,   522,   520,   518,   516,   514,   512,   510,
   508,   506,   504,   502,   500,   498,   496,   494,
   492,   490,   488,   488,   486,   484,   482,   480,
   478,   476,   474,   474,   472,   470,   468,   466,
   464,   462,   462,   460,   458,   456,   454,   454,
   452,   450,   448,   448,   446,   444,   442,   442,
   440,   438,   436,   436,   434,   432,   430,   430,
   428,   426,   426,   424,   422,   422,   420,   418,
   418,   416,   414,   414,   412,   410,   410,   408,
   406,   406,   404,   402,   402,   400,   400,   398,
   396,   396,   394,   394,   392,   390,   390,   388,
   388,   386,   386,   384,   382,   382,   380,   380,
   378,   378,   376,   376,   374,   372,   372,   370,
   370,   368,   368,   366,   366,   364,   364,   362,
   362,   360,   360,   358,   358,   356,   356,   354,
   354,   352,   352,   350,   350,   348,   348,   348,
   346,   346,   344,   344,   342,   342,   340,   340,
   340,   338,   338,   336,   336,   334,   334,   332,
   332,   332,   330,   330,   328,   328,   328,   326,
   326,   324,   324,   324,   322,   322,   320,   320,
   320,   318,   318,   316,   316,   316,   314,   314,
   312,   312,   312,   310,   310,   310,   308,   308,
   308,   306,   306,   304,   304,   304,   302,   302,
   302,   300,   300,   300,   298,   298,   298,   296,
   296,   296,   294,   294,   294,   292,   292,   292,
   290,   290,   290,   288,   288,   288,   286,   286,
   286,   284,   284,   284,   284,   282,   282,   282,
   280,   280,   280,   278,   278,   278,   276,   276,
   276,   276,   274,   274,   274,   272,   272,   272,
   272,   270,   270,   270,   268,   268,   268,   268,
   266,   266,   266,   266,   264,   264,   264,   262,
   262,   262,   262,   260,   260,   260,   260,   258,
   258,   258,   258,   256,   256,   256,   256,   254,
   254,   254,   254,   252,   252,   252,   252,   250,
   250,   250,   250,   248,   248,   248,   248,   248,
   246,   246,   246,   246,   244,   244,   244,   244,
   242,   242,   242,   242,   242,   240,   240,   240,
   240,   238,   238,   238,   238,   238,   236,   236,
   236,   236,   234,   234,   234,   234,   234,   232,
   232,   232,   232,   232,   230,   230,   230,   230,
   230,   228,   228,   228,   228,   228,   226,   226,
   226,   226,   226,   224,   224,   224,   224,   224,
   222,   222,   222,   222,   222,   220,   220,   220,
   220,   220,   220,   218,   218,   218,   218,   218,
   216,   216,   216,   216,   216,   216,   214,   214,
   214,   214,   214,   212,   212,   212,   212,   212,
   212,   210,   210,   210,   210,   210,   210,   208,
   208,   208,   208,   208,   208,   206,   206,   206,
   206,   206,   206,   204,   204,   204,   204,   204,
   204,   204,   202,   202,   202,   202,   202,   202,
   200,   200,   200,   200,   200,   200,   198,   198,
   198,   198,   198,   198,   198,   196,   196,   196,
   196,   196,   196,   196,   194,   194,   194,   194,
   194,   194,   194,   192,   192,   192,   192,   192,
   192,   192,   190,   190,   190,   190,   190,   190,
   190,   188,   188,   188,   188,   188,   188,   188,
   186,   186,   186,   186,   186,   186,   186,   186,
   184,   184,   184,   184,   184,   184,   184,   182,
   182,   182,   182,   182,   182,   182,   182,   180,
   180,   180,   180,   180,   180,   180,   180,   178,
   178,   178,   178,   178,   178,   178,   178,   176,
   176,   176,   176,   176,   176,   176,   176,   176,
   174,   174,   174,   174,   174,   174,   174,   174,
   172,   172,   172,   172,   172,   172,   172,   172,
   172,   170,   170,   170,   170,   170,   170,   170,
   170,   170,   168,   168,   168,   168,   168,   168,
   168,   168,   168,   166,   166,   166,   166,   166,
   166,   166,   166,   166,   166,   164,   164,   164,
   164,   164,   164,   164,   164,   164,   162,   162,
   162,   162,   162,   162,   162,   162,   162,   162,
   160,   160,   160,   160,   160,   160,   160,   160,
   160,   160,   158,   158,   158,   158,   158,   158,
   158,   158,   158,   158,   158,   156,   156,   156,
   156,   156,   156,   156,   156,   156,   156,   154,
   154,   154,   154,   154,   154,   154,   154,   154,
   154,   154,   152,   152,   152,   152,   152,   152,
   152,   152,   152,   152,   152,   150,   150,   150,
   150,   150,   150,   150,   150,   150,   150,   150,
   150,   148,   148,   148,   148,   148,   148,   148,
   148,   148,   148,   148,   148,   146,   146,   146,
   146,   146,   146,   146,   146,   146,   146,   146,
   146,   144,   144,   144,   144,   144,   144,   144,
   144,   144,   144,   144,   144,   142,   142,   142,
   142,   142,   142,   142,   142,   142,   142,   142,
   142,   142,   140,   140,   140,   140,   140,   140,
   140,   140,   140,   140,   140,   140,   140,   138,
   138,   138,   138,   138,   138,   138,   138,   138,
   138,   138,   138,   138,   138,   136,   136,   136,
   136,   136,   136,   136,   136,   136,   136,   136,
   136,   136,   136,   134,   134,   134,   134,   134,
   134,   134,   134,   134,   134,   134,   134,   134,
   134,   132,   132,   132,   132,   132,   132,   132,
   132,   132,   132,   132,   132,   132,   132,   132,
   130,   130,   130,   130,   130,   130,   130,   130,
   130,   130,   130,   130,   130,   130,   130,   128,
   128,   128,   128,   128,   128,   128,   128,   128,
   128,   128,   128,   128,   128,   128,   128,   126
};

		// ssquasht[i]=int(32768.0/(1+exp((i-2048)*(-1.0/64))));
		// Middle 1344 of 4096 entries only.
		static ushort[] ssquasht = new ushort[1344] {
	 0,     0,     0,     0,     0,     0,     0,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     2,     2,     2,     2,     2,
	 2,     2,     2,     2,     2,     2,     2,     2,
	 2,     2,     2,     2,     2,     2,     2,     2,
	 2,     2,     2,     2,     2,     3,     3,     3,
	 3,     3,     3,     3,     3,     3,     3,     3,
	 3,     3,     3,     3,     3,     3,     3,     3,
	 4,     4,     4,     4,     4,     4,     4,     4,
	 4,     4,     4,     4,     4,     4,     5,     5,
	 5,     5,     5,     5,     5,     5,     5,     5,
	 5,     5,     6,     6,     6,     6,     6,     6,
	 6,     6,     6,     6,     7,     7,     7,     7,
	 7,     7,     7,     7,     8,     8,     8,     8,
	 8,     8,     8,     8,     9,     9,     9,     9,
	 9,     9,    10,    10,    10,    10,    10,    10,
	10,    11,    11,    11,    11,    11,    12,    12,
	12,    12,    12,    13,    13,    13,    13,    13,
	14,    14,    14,    14,    15,    15,    15,    15,
	15,    16,    16,    16,    17,    17,    17,    17,
	18,    18,    18,    18,    19,    19,    19,    20,
	20,    20,    21,    21,    21,    22,    22,    22,
	23,    23,    23,    24,    24,    25,    25,    25,
	26,    26,    27,    27,    28,    28,    28,    29,
	29,    30,    30,    31,    31,    32,    32,    33,
	33,    34,    34,    35,    36,    36,    37,    37,
	38,    38,    39,    40,    40,    41,    42,    42,
	43,    44,    44,    45,    46,    46,    47,    48,
	49,    49,    50,    51,    52,    53,    54,    54,
	55,    56,    57,    58,    59,    60,    61,    62,
	63,    64,    65,    66,    67,    68,    69,    70,
	71,    72,    73,    74,    76,    77,    78,    79,
	81,    82,    83,    84,    86,    87,    88,    90,
	91,    93,    94,    96,    97,    99,   100,   102,
   103,   105,   107,   108,   110,   112,   114,   115,
   117,   119,   121,   123,   125,   127,   129,   131,
   133,   135,   137,   139,   141,   144,   146,   148,
   151,   153,   155,   158,   160,   163,   165,   168,
   171,   173,   176,   179,   182,   184,   187,   190,
   193,   196,   199,   202,   206,   209,   212,   215,
   219,   222,   226,   229,   233,   237,   240,   244,
   248,   252,   256,   260,   264,   268,   272,   276,
   281,   285,   289,   294,   299,   303,   308,   313,
   318,   323,   328,   333,   338,   343,   349,   354,
   360,   365,   371,   377,   382,   388,   394,   401,
   407,   413,   420,   426,   433,   440,   446,   453,
   460,   467,   475,   482,   490,   497,   505,   513,
   521,   529,   537,   545,   554,   562,   571,   580,
   589,   598,   607,   617,   626,   636,   646,   656,
   666,   676,   686,   697,   708,   719,   730,   741,
   752,   764,   776,   788,   800,   812,   825,   837,
   850,   863,   876,   890,   903,   917,   931,   946,
   960,   975,   990,  1005,  1020,  1036,  1051,  1067,
  1084,  1100,  1117,  1134,  1151,  1169,  1186,  1204,
  1223,  1241,  1260,  1279,  1298,  1318,  1338,  1358,
  1379,  1399,  1421,  1442,  1464,  1486,  1508,  1531,
  1554,  1577,  1600,  1624,  1649,  1673,  1698,  1724,
  1749,  1775,  1802,  1829,  1856,  1883,  1911,  1940,
  1968,  1998,  2027,  2057,  2087,  2118,  2149,  2181,
  2213,  2245,  2278,  2312,  2345,  2380,  2414,  2450,
  2485,  2521,  2558,  2595,  2633,  2671,  2709,  2748,
  2788,  2828,  2869,  2910,  2952,  2994,  3037,  3080,
  3124,  3168,  3213,  3259,  3305,  3352,  3399,  3447,
  3496,  3545,  3594,  3645,  3696,  3747,  3799,  3852,
  3906,  3960,  4014,  4070,  4126,  4182,  4240,  4298,
  4356,  4416,  4476,  4537,  4598,  4660,  4723,  4786,
  4851,  4916,  4981,  5048,  5115,  5183,  5251,  5320,
  5390,  5461,  5533,  5605,  5678,  5752,  5826,  5901,
  5977,  6054,  6131,  6210,  6289,  6369,  6449,  6530,
  6613,  6695,  6779,  6863,  6949,  7035,  7121,  7209,
  7297,  7386,  7476,  7566,  7658,  7750,  7842,  7936,
  8030,  8126,  8221,  8318,  8415,  8513,  8612,  8712,
  8812,  8913,  9015,  9117,  9221,  9324,  9429,  9534,
  9640,  9747,  9854,  9962, 10071, 10180, 10290, 10401,
 10512, 10624, 10737, 10850, 10963, 11078, 11192, 11308,
 11424, 11540, 11658, 11775, 11893, 12012, 12131, 12251,
 12371, 12491, 12612, 12734, 12856, 12978, 13101, 13224,
 13347, 13471, 13595, 13719, 13844, 13969, 14095, 14220,
 14346, 14472, 14599, 14725, 14852, 14979, 15106, 15233,
 15361, 15488, 15616, 15744, 15872, 16000, 16128, 16256,
 16384, 16511, 16639, 16767, 16895, 17023, 17151, 17279,
 17406, 17534, 17661, 17788, 17915, 18042, 18168, 18295,
 18421, 18547, 18672, 18798, 18923, 19048, 19172, 19296,
 19420, 19543, 19666, 19789, 19911, 20033, 20155, 20276,
 20396, 20516, 20636, 20755, 20874, 20992, 21109, 21227,
 21343, 21459, 21575, 21689, 21804, 21917, 22030, 22143,
 22255, 22366, 22477, 22587, 22696, 22805, 22913, 23020,
 23127, 23233, 23338, 23443, 23546, 23650, 23752, 23854,
 23955, 24055, 24155, 24254, 24352, 24449, 24546, 24641,
 24737, 24831, 24925, 25017, 25109, 25201, 25291, 25381,
 25470, 25558, 25646, 25732, 25818, 25904, 25988, 26072,
 26154, 26237, 26318, 26398, 26478, 26557, 26636, 26713,
 26790, 26866, 26941, 27015, 27089, 27162, 27234, 27306,
 27377, 27447, 27516, 27584, 27652, 27719, 27786, 27851,
 27916, 27981, 28044, 28107, 28169, 28230, 28291, 28351,
 28411, 28469, 28527, 28585, 28641, 28697, 28753, 28807,
 28861, 28915, 28968, 29020, 29071, 29122, 29173, 29222,
 29271, 29320, 29368, 29415, 29462, 29508, 29554, 29599,
 29643, 29687, 29730, 29773, 29815, 29857, 29898, 29939,
 29979, 30019, 30058, 30096, 30134, 30172, 30209, 30246,
 30282, 30317, 30353, 30387, 30422, 30455, 30489, 30522,
 30554, 30586, 30618, 30649, 30680, 30710, 30740, 30769,
 30799, 30827, 30856, 30884, 30911, 30938, 30965, 30992,
 31018, 31043, 31069, 31094, 31118, 31143, 31167, 31190,
 31213, 31236, 31259, 31281, 31303, 31325, 31346, 31368,
 31388, 31409, 31429, 31449, 31469, 31488, 31507, 31526,
 31544, 31563, 31581, 31598, 31616, 31633, 31650, 31667,
 31683, 31700, 31716, 31731, 31747, 31762, 31777, 31792,
 31807, 31821, 31836, 31850, 31864, 31877, 31891, 31904,
 31917, 31930, 31942, 31955, 31967, 31979, 31991, 32003,
 32015, 32026, 32037, 32048, 32059, 32070, 32081, 32091,
 32101, 32111, 32121, 32131, 32141, 32150, 32160, 32169,
 32178, 32187, 32196, 32205, 32213, 32222, 32230, 32238,
 32246, 32254, 32262, 32270, 32277, 32285, 32292, 32300,
 32307, 32314, 32321, 32327, 32334, 32341, 32347, 32354,
 32360, 32366, 32373, 32379, 32385, 32390, 32396, 32402,
 32407, 32413, 32418, 32424, 32429, 32434, 32439, 32444,
 32449, 32454, 32459, 32464, 32468, 32473, 32478, 32482,
 32486, 32491, 32495, 32499, 32503, 32507, 32511, 32515,
 32519, 32523, 32527, 32530, 32534, 32538, 32541, 32545,
 32548, 32552, 32555, 32558, 32561, 32565, 32568, 32571,
 32574, 32577, 32580, 32583, 32585, 32588, 32591, 32594,
 32596, 32599, 32602, 32604, 32607, 32609, 32612, 32614,
 32616, 32619, 32621, 32623, 32626, 32628, 32630, 32632,
 32634, 32636, 32638, 32640, 32642, 32644, 32646, 32648,
 32650, 32652, 32653, 32655, 32657, 32659, 32660, 32662,
 32664, 32665, 32667, 32668, 32670, 32671, 32673, 32674,
 32676, 32677, 32679, 32680, 32681, 32683, 32684, 32685,
 32686, 32688, 32689, 32690, 32691, 32693, 32694, 32695,
 32696, 32697, 32698, 32699, 32700, 32701, 32702, 32703,
 32704, 32705, 32706, 32707, 32708, 32709, 32710, 32711,
 32712, 32713, 32713, 32714, 32715, 32716, 32717, 32718,
 32718, 32719, 32720, 32721, 32721, 32722, 32723, 32723,
 32724, 32725, 32725, 32726, 32727, 32727, 32728, 32729,
 32729, 32730, 32730, 32731, 32731, 32732, 32733, 32733,
 32734, 32734, 32735, 32735, 32736, 32736, 32737, 32737,
 32738, 32738, 32739, 32739, 32739, 32740, 32740, 32741,
 32741, 32742, 32742, 32742, 32743, 32743, 32744, 32744,
 32744, 32745, 32745, 32745, 32746, 32746, 32746, 32747,
 32747, 32747, 32748, 32748, 32748, 32749, 32749, 32749,
 32749, 32750, 32750, 32750, 32750, 32751, 32751, 32751,
 32752, 32752, 32752, 32752, 32752, 32753, 32753, 32753,
 32753, 32754, 32754, 32754, 32754, 32754, 32755, 32755,
 32755, 32755, 32755, 32756, 32756, 32756, 32756, 32756,
 32757, 32757, 32757, 32757, 32757, 32757, 32757, 32758,
 32758, 32758, 32758, 32758, 32758, 32759, 32759, 32759,
 32759, 32759, 32759, 32759, 32759, 32760, 32760, 32760,
 32760, 32760, 32760, 32760, 32760, 32761, 32761, 32761,
 32761, 32761, 32761, 32761, 32761, 32761, 32761, 32762,
 32762, 32762, 32762, 32762, 32762, 32762, 32762, 32762,
 32762, 32762, 32762, 32763, 32763, 32763, 32763, 32763,
 32763, 32763, 32763, 32763, 32763, 32763, 32763, 32763,
 32763, 32764, 32764, 32764, 32764, 32764, 32764, 32764,
 32764, 32764, 32764, 32764, 32764, 32764, 32764, 32764,
 32764, 32764, 32764, 32764, 32765, 32765, 32765, 32765,
 32765, 32765, 32765, 32765, 32765, 32765, 32765, 32765,
 32765, 32765, 32765, 32765, 32765, 32765, 32765, 32765,
 32765, 32765, 32765, 32765, 32765, 32765, 32766, 32766,
 32766, 32766, 32766, 32766, 32766, 32766, 32766, 32766,
 32766, 32766, 32766, 32766, 32766, 32766, 32766, 32766,
 32766, 32766, 32766, 32766, 32766, 32766, 32766, 32766,
 32766, 32766, 32766, 32766, 32766, 32766, 32766, 32766,
 32766, 32766, 32766, 32766, 32766, 32766, 32766, 32766,
 32766, 32766, 32767, 32767, 32767, 32767, 32767, 32767
};

		// stdt[i]=count of -i or i in botton or top of stretcht[]
		static byte[] stdt = new byte[712] {
	64,   128,   128,   128,   128,   128,   127,   128,
   127,   128,   127,   127,   127,   127,   126,   126,
   126,   126,   126,   125,   125,   124,   125,   124,
   123,   123,   123,   123,   122,   122,   121,   121,
   120,   120,   119,   119,   118,   118,   118,   116,
   117,   115,   116,   114,   114,   113,   113,   112,
   112,   111,   110,   110,   109,   108,   108,   107,
   106,   106,   105,   104,   104,   102,   103,   101,
   101,   100,    99,    98,    98,    97,    96,    96,
	94,    94,    94,    92,    92,    91,    90,    89,
	89,    88,    87,    86,    86,    84,    84,    84,
	82,    82,    81,    80,    79,    79,    78,    77,
	76,    76,    75,    74,    73,    73,    72,    71,
	70,    70,    69,    68,    67,    67,    66,    65,
	65,    64,    63,    62,    62,    61,    61,    59,
	59,    59,    57,    58,    56,    56,    55,    54,
	54,    53,    52,    52,    51,    51,    50,    49,
	49,    48,    48,    47,    47,    45,    46,    44,
	45,    43,    43,    43,    42,    41,    41,    40,
	40,    40,    39,    38,    38,    37,    37,    36,
	36,    36,    35,    34,    34,    34,    33,    32,
	33,    32,    31,    31,    30,    31,    29,    30,
	28,    29,    28,    28,    27,    27,    27,    26,
	26,    25,    26,    24,    25,    24,    24,    23,
	23,    23,    23,    22,    22,    21,    22,    21,
	20,    21,    20,    19,    20,    19,    19,    19,
	18,    18,    18,    18,    17,    17,    17,    17,
	16,    16,    16,    16,    15,    15,    15,    15,
	15,    14,    14,    14,    14,    13,    14,    13,
	13,    13,    12,    13,    12,    12,    12,    11,
	12,    11,    11,    11,    11,    11,    10,    11,
	10,    10,    10,    10,     9,    10,     9,     9,
	 9,     9,     9,     8,     9,     8,     9,     8,
	 8,     8,     7,     8,     8,     7,     7,     8,
	 7,     7,     7,     6,     7,     7,     6,     6,
	 7,     6,     6,     6,     6,     6,     6,     5,
	 6,     5,     6,     5,     5,     5,     5,     5,
	 5,     5,     5,     5,     4,     5,     4,     5,
	 4,     4,     5,     4,     4,     4,     4,     4,
	 4,     3,     4,     4,     3,     4,     4,     3,
	 3,     4,     3,     3,     3,     4,     3,     3,
	 3,     3,     3,     3,     2,     3,     3,     3,
	 2,     3,     2,     3,     3,     2,     2,     3,
	 2,     2,     3,     2,     2,     2,     2,     3,
	 2,     2,     2,     2,     2,     2,     1,     2,
	 2,     2,     2,     1,     2,     2,     2,     1,
	 2,     1,     2,     2,     1,     2,     1,     2,
	 1,     1,     2,     1,     1,     2,     1,     1,
	 2,     1,     1,     1,     1,     2,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     1,     1,     1,     1,     1,     1,
	 1,     1,     0,     1,     1,     1,     1,     0,
	 1,     1,     1,     0,     1,     1,     1,     0,
	 1,     1,     0,     1,     1,     0,     1,     0,
	 1,     1,     0,     1,     0,     1,     0,     1,
	 0,     1,     0,     1,     0,     1,     0,     1,
	 0,     1,     0,     1,     0,     1,     0,     0,
	 1,     0,     1,     0,     0,     1,     0,     1,
	 0,     0,     1,     0,     0,     1,     0,     0,
	 1,     0,     0,     1,     0,     0,     0,     1,
	 0,     0,     1,     0,     0,     0,     1,     0,
	 0,     0,     1,     0,     0,     0,     1,     0,
	 0,     0,     0,     1,     0,     0,     0,     0,
	 1,     0,     0,     0,     0,     1,     0,     0,
	 0,     0,     0,     1,     0,     0,     0,     0,
	 0,     1,     0,     0,     0,     0,     0,     0,
	 1,     0,     0,     0,     0,     0,     0,     0,
	 1,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     1,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     1,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     1,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     1,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     1,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     1,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     0,     0,
	 0,     0,     0,     0,     0,     0,     1,     0
};
	}
}
