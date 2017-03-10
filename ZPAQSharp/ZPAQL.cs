using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using U8 = System.Byte;
using U16 = System.UInt16;
using U32 = System.UInt32;
using U64 = System.UInt64;

namespace ZPAQSharp
{
	class ZPAQL
	{
		public ZPAQL()
		{
			output = 0;
			sha1 = 0;
			rcode = 0;
			rcode_size = 0;
			clear();
			outbuf.resize(1 << 14);
			bufptr = 0;
		}

		~ZPAQL()
		{
			allocx(rcode, rcode_size, 0);
		}

		public void clear() // Free memory, erase program, reset machine state
		{
			cend = hbegin = hend = 0;  // COMP and HCOMP locations
			a = b = c = d = f = pc = 0;      // machine state
			header.resize(0);
			h.resize(0);
			m.resize(0);
			r.resize(0);
			allocx(rcode, rcode_size, 0);
		}

		public void inith() // Initialize as HCOMP to run
		{
			assert(header.isize() > 6);
			assert(output == 0);
			assert(sha1 == 0);
			init(header[2], header[3]); // hh, hm
		}

		public void initp() // Initialize as PCOMP to run
		{
			assert(header.isize() > 6);
			init(header[4], header[5]); // ph, pm
		}

		public double memory() // Return memory requirement in bytes
		{
			double mem = pow2(header[2] + 2) + pow2(header[3])  // hh hm
			+ pow2(header[4] + 2) + pow2(header[5])  // ph pm
			+ header.size();
			int cp = 7;  // start of comp list
			for (int i = 0; i < header[6]; ++i)
			{  // n
				assert(cp < cend);
				double size = pow2(header[cp + 1]); // sizebits
				switch (header[cp])
				{
					case CM: mem += 4 * size; break;
					case ICM: mem += 64 * size + 1024; break;
					case MATCH: mem += 4 * size + pow2(header[cp + 2]); break; // bufbits
					case MIX2: mem += 2 * size; break;
					case MIX: mem += 4 * size * header[cp + 3]; break; // m
					case ISSE: mem += 64 * size + 2048; break;
					case SSE: mem += 128 * size; break;
				}
				cp += compsize[header[cp]];
			}
			return mem;
		}

		public void run(U32 input) // Execute with input
		{
		}

		public int read(Reader in2) // Read header
		{
			/ Get header size and allocate
  int hsize = in2->get();
			hsize += in2->get() * 256;
			header.resize(hsize + 300);
			cend = hbegin = hend = 0;
			header[cend++] = hsize & 255;
			header[cend++] = hsize >> 8;
			while (cend < 7) header[cend++] = in2->get(); // hh hm ph pm n

			// Read COMP
			int n = header[cend - 1];
			for (int i = 0; i < n; ++i)
			{
				int type = in2->get();  // component type
				if (type < 0 || type > 255) error("unexpected end of file");
				header[cend++] = type;  // component type
				int size = compsize[type];
				if (size < 1) error("Invalid component type");
				if (cend + size > hsize) error("COMP overflows header");
				for (int j = 1; j < size; ++j)
					header[cend++] = in2->get();
			}
			if ((header[cend++] = in2->get()) != 0) error("missing COMP END");

			// Insert a guard gap and read HCOMP
			hbegin = hend = cend + 128;
			if (hend > hsize + 129) error("missing HCOMP");
			while (hend < hsize + 129)
			{
				assert(hend < header.isize() - 8);
				int op = in2->get();
				if (op == -1) error("unexpected end of file");
				header[hend++] = op;
			}
			if ((header[hend++] = in2->get()) != 0) error("missing HCOMP END");
			assert(cend >= 7 && cend < header.isize());
			assert(hbegin == cend + 128 && hbegin < header.isize());
			assert(hend > hbegin && hend < header.isize());
			assert(hsize == header[0] + 256 * header[1]);
			assert(hsize == cend - 2 + hend - hbegin);
			allocx(rcode, rcode_size, 0);  // clear JIT code
			return cend + hend - hbegin;
		}

		public bool write(Writer out2, bool pp) // If pp write PCOMP else HCOMP header
		{
			if (header.size() <= 6) return false;
			assert(header[0] + 256 * header[1] == cend - 2 + hend - hbegin);
			assert(cend >= 7);
			assert(hbegin >= cend);
			assert(hend >= hbegin);
			assert(out2);
			if (!pp)
			{  // if not a postprocessor then write COMP
				for (int i = 0; i < cend; ++i)
					out2->put(header[i]);
			}
			else
			{  // write PCOMP size only
				out2->put((hend - hbegin) & 255);
				out2->put((hend - hbegin) >> 8);
			}
			for (int i = hbegin; i < hend; ++i)
				out2->put(header[i]);
			return true;
		}

		public int step(U32 input, int mode) // Trace execution (defined externally)
		{
			return 0;
		}

		public Writer output;   // Destination for OUT instruction, or 0 to suppress
		public SHA1 sha1;       // Points to checksum computer
		
		public U32 H(int i)		// get element of h
		{
			return h[i];
		}

		public void flush() // write outbuf[0..bufptr-1] to output and sha1
		{
			if (output) output->write(&outbuf[0], bufptr);
			if (sha1) sha1->write(&outbuf[0], bufptr);
			bufptr = 0;
		}

		public void outc(int ch) // output byte ch (0..255) or -1 at EOS
		{
			if (ch < 0 || (outbuf[bufptr] = ch && ++bufptr == outbuf.isize()))
			{
				flush();
			}
		}
		// ZPAQ1 block header
		public Array<U8> header;   // hsize[2] hh hm ph pm n COMP (guard) HCOMP (guard)
		public int cend;           // COMP in header[7...cend-1]
		public int hbegin, hend;   // HCOMP/PCOMP in header[hbegin...hend-1]

		// Machine state for executing HCOMP
		private Array<U8> m;        // memory array M for HCOMP
		private Array<U32> h;       // hash array H for HCOMP
		private Array<U32> r;       // 256 element register array
		private Array<char> outbuf; // output buffer
		private int bufptr;         // number of bytes in outbuf
		private U32 a, b, c, d;     // machine registers
		private int f;              // condition flag
		private int pc;             // program counter
		private int rcode_size;     // length of rcode
		private U8[] rcode;         // JIT code for run()

		// Support code
		private int assemble() // put JIT code in rcode
		{
			return 0;
		}

		private void init(int hbits, int mbits) // initialize H and M sizes
		{
			assert(header.isize() > 0);
			assert(cend >= 7);
			assert(hbegin >= cend + 128);
			assert(hend >= hbegin);
			assert(hend < header.isize() - 130);
			assert(header[0] + 256 * header[1] == cend - 2 + hend - hbegin);
			assert(bufptr == 0);
			assert(outbuf.isize() > 0);
			if (hbits > 32) error("H too big");
			if (mbits > 32) error("M too big");
			h.resize(1, hbits);
			m.resize(1, mbits);
			r.resize(256);
			a = b = c = d = pc = f = 0;
		}

		private int execute() // interpret 1 instruction, return 0 after HALT, else 1
		{
			switch (header[pc++])
			{
				case 0: err(); break; // ERROR
				case 1: ++a; break; // A++
				case 2: --a; break; // A--
				case 3: a = ~a; break; // A!
				case 4: a = 0; break; // A=0
				case 7: a = r[header[pc++]]; break; // A=R N
				case 8: swap(b); break; // B<>A
				case 9: ++b; break; // B++
				case 10: --b; break; // B--
				case 11: b = ~b; break; // B!
				case 12: b = 0; break; // B=0
				case 15: b = r[header[pc++]]; break; // B=R N
				case 16: swap(c); break; // C<>A
				case 17: ++c; break; // C++
				case 18: --c; break; // C--
				case 19: c = ~c; break; // C!
				case 20: c = 0; break; // C=0
				case 23: c = r[header[pc++]]; break; // C=R N
				case 24: swap(d); break; // D<>A
				case 25: ++d; break; // D++
				case 26: --d; break; // D--
				case 27: d = ~d; break; // D!
				case 28: d = 0; break; // D=0
				case 31: d = r[header[pc++]]; break; // D=R N
				case 32: swap(m(b)); break; // *B<>A
				case 33: ++m(b); break; // *B++
				case 34: --m(b); break; // *B--
				case 35: m(b) = ~m(b); break; // *B!
				case 36: m(b) = 0; break; // *B=0
				case 39: if (f) pc += ((header[pc] + 128) & 255) - 127; else ++pc; break; // JT N
				case 40: swap(m(c)); break; // *C<>A
				case 41: ++m(c); break; // *C++
				case 42: --m(c); break; // *C--
				case 43: m(c) = ~m(c); break; // *C!
				case 44: m(c) = 0; break; // *C=0
				case 47: if (!f) pc += ((header[pc] + 128) & 255) - 127; else ++pc; break; // JF N
				case 48: swap(h(d)); break; // *D<>A
				case 49: ++h(d); break; // *D++
				case 50: --h(d); break; // *D--
				case 51: h(d) = ~h(d); break; // *D!
				case 52: h(d) = 0; break; // *D=0
				case 55: r[header[pc++]] = a; break; // R=A N
				case 56: return 0; // HALT
				case 57: outc(a & 255); break; // OUT
				case 59: a = (a + m(b) + 512) * 773; break; // HASH
				case 60: h(d) = (h(d) + a + 512) * 773; break; // HASHD
				case 63: pc += ((header[pc] + 128) & 255) - 127; break; // JMP N
				case 64: break; // A=A
				case 65: a = b; break; // A=B
				case 66: a = c; break; // A=C
				case 67: a = d; break; // A=D
				case 68: a = m(b); break; // A=*B
				case 69: a = m(c); break; // A=*C
				case 70: a = h(d); break; // A=*D
				case 71: a = header[pc++]; break; // A= N
				case 72: b = a; break; // B=A
				case 73: break; // B=B
				case 74: b = c; break; // B=C
				case 75: b = d; break; // B=D
				case 76: b = m(b); break; // B=*B
				case 77: b = m(c); break; // B=*C
				case 78: b = h(d); break; // B=*D
				case 79: b = header[pc++]; break; // B= N
				case 80: c = a; break; // C=A
				case 81: c = b; break; // C=B
				case 82: break; // C=C
				case 83: c = d; break; // C=D
				case 84: c = m(b); break; // C=*B
				case 85: c = m(c); break; // C=*C
				case 86: c = h(d); break; // C=*D
				case 87: c = header[pc++]; break; // C= N
				case 88: d = a; break; // D=A
				case 89: d = b; break; // D=B
				case 90: d = c; break; // D=C
				case 91: break; // D=D
				case 92: d = m(b); break; // D=*B
				case 93: d = m(c); break; // D=*C
				case 94: d = h(d); break; // D=*D
				case 95: d = header[pc++]; break; // D= N
				case 96: m(b) = a; break; // *B=A
				case 97: m(b) = b; break; // *B=B
				case 98: m(b) = c; break; // *B=C
				case 99: m(b) = d; break; // *B=D
				case 100: break; // *B=*B
				case 101: m(b) = m(c); break; // *B=*C
				case 102: m(b) = h(d); break; // *B=*D
				case 103: m(b) = header[pc++]; break; // *B= N
				case 104: m(c) = a; break; // *C=A
				case 105: m(c) = b; break; // *C=B
				case 106: m(c) = c; break; // *C=C
				case 107: m(c) = d; break; // *C=D
				case 108: m(c) = m(b); break; // *C=*B
				case 109: break; // *C=*C
				case 110: m(c) = h(d); break; // *C=*D
				case 111: m(c) = header[pc++]; break; // *C= N
				case 112: h(d) = a; break; // *D=A
				case 113: h(d) = b; break; // *D=B
				case 114: h(d) = c; break; // *D=C
				case 115: h(d) = d; break; // *D=D
				case 116: h(d) = m(b); break; // *D=*B
				case 117: h(d) = m(c); break; // *D=*C
				case 118: break; // *D=*D
				case 119: h(d) = header[pc++]; break; // *D= N
				case 128: a += a; break; // A+=A
				case 129: a += b; break; // A+=B
				case 130: a += c; break; // A+=C
				case 131: a += d; break; // A+=D
				case 132: a += m(b); break; // A+=*B
				case 133: a += m(c); break; // A+=*C
				case 134: a += h(d); break; // A+=*D
				case 135: a += header[pc++]; break; // A+= N
				case 136: a -= a; break; // A-=A
				case 137: a -= b; break; // A-=B
				case 138: a -= c; break; // A-=C
				case 139: a -= d; break; // A-=D
				case 140: a -= m(b); break; // A-=*B
				case 141: a -= m(c); break; // A-=*C
				case 142: a -= h(d); break; // A-=*D
				case 143: a -= header[pc++]; break; // A-= N
				case 144: a *= a; break; // A*=A
				case 145: a *= b; break; // A*=B
				case 146: a *= c; break; // A*=C
				case 147: a *= d; break; // A*=D
				case 148: a *= m(b); break; // A*=*B
				case 149: a *= m(c); break; // A*=*C
				case 150: a *= h(d); break; // A*=*D
				case 151: a *= header[pc++]; break; // A*= N
				case 152: div(a); break; // A/=A
				case 153: div(b); break; // A/=B
				case 154: div(c); break; // A/=C
				case 155: div(d); break; // A/=D
				case 156: div(m(b)); break; // A/=*B
				case 157: div(m(c)); break; // A/=*C
				case 158: div(h(d)); break; // A/=*D
				case 159: div(header[pc++]); break; // A/= N
				case 160: mod(a); break; // A%=A
				case 161: mod(b); break; // A%=B
				case 162: mod(c); break; // A%=C
				case 163: mod(d); break; // A%=D
				case 164: mod(m(b)); break; // A%=*B
				case 165: mod(m(c)); break; // A%=*C
				case 166: mod(h(d)); break; // A%=*D
				case 167: mod(header[pc++]); break; // A%= N
				case 168: a &= a; break; // A&=A
				case 169: a &= b; break; // A&=B
				case 170: a &= c; break; // A&=C
				case 171: a &= d; break; // A&=D
				case 172: a &= m(b); break; // A&=*B
				case 173: a &= m(c); break; // A&=*C
				case 174: a &= h(d); break; // A&=*D
				case 175: a &= header[pc++]; break; // A&= N
				case 176: a &= ~a; break; // A&~A
				case 177: a &= ~b; break; // A&~B
				case 178: a &= ~c; break; // A&~C
				case 179: a &= ~d; break; // A&~D
				case 180: a &= ~m(b); break; // A&~*B
				case 181: a &= ~m(c); break; // A&~*C
				case 182: a &= ~h(d); break; // A&~*D
				case 183: a &= ~header[pc++]; break; // A&~ N
				case 184: a |= a; break; // A|=A
				case 185: a |= b; break; // A|=B
				case 186: a |= c; break; // A|=C
				case 187: a |= d; break; // A|=D
				case 188: a |= m(b); break; // A|=*B
				case 189: a |= m(c); break; // A|=*C
				case 190: a |= h(d); break; // A|=*D
				case 191: a |= header[pc++]; break; // A|= N
				case 192: a ^= a; break; // A^=A
				case 193: a ^= b; break; // A^=B
				case 194: a ^= c; break; // A^=C
				case 195: a ^= d; break; // A^=D
				case 196: a ^= m(b); break; // A^=*B
				case 197: a ^= m(c); break; // A^=*C
				case 198: a ^= h(d); break; // A^=*D
				case 199: a ^= header[pc++]; break; // A^= N
				case 200: a <<= (a & 31); break; // A<<=A
				case 201: a <<= (b & 31); break; // A<<=B
				case 202: a <<= (c & 31); break; // A<<=C
				case 203: a <<= (d & 31); break; // A<<=D
				case 204: a <<= (m(b) & 31); break; // A<<=*B
				case 205: a <<= (m(c) & 31); break; // A<<=*C
				case 206: a <<= (h(d) & 31); break; // A<<=*D
				case 207: a <<= (header[pc++] & 31); break; // A<<= N
				case 208: a >>= (a & 31); break; // A>>=A
				case 209: a >>= (b & 31); break; // A>>=B
				case 210: a >>= (c & 31); break; // A>>=C
				case 211: a >>= (d & 31); break; // A>>=D
				case 212: a >>= (m(b) & 31); break; // A>>=*B
				case 213: a >>= (m(c) & 31); break; // A>>=*C
				case 214: a >>= (h(d) & 31); break; // A>>=*D
				case 215: a >>= (header[pc++] & 31); break; // A>>= N
				case 216: f = 1; break; // A==A
				case 217: f = (a == b); break; // A==B
				case 218: f = (a == c); break; // A==C
				case 219: f = (a == d); break; // A==D
				case 220: f = (a == U32(m(b))); break; // A==*B
				case 221: f = (a == U32(m(c))); break; // A==*C
				case 222: f = (a == h(d)); break; // A==*D
				case 223: f = (a == U32(header[pc++])); break; // A== N
				case 224: f = 0; break; // A<A
				case 225: f = (a < b); break; // A<B
				case 226: f = (a < c); break; // A<C
				case 227: f = (a < d); break; // A<D
				case 228: f = (a < U32(m(b))); break; // A<*B
				case 229: f = (a < U32(m(c))); break; // A<*C
				case 230: f = (a < h(d)); break; // A<*D
				case 231: f = (a < U32(header[pc++])); break; // A< N
				case 232: f = 0; break; // A>A
				case 233: f = (a > b); break; // A>B
				case 234: f = (a > c); break; // A>C
				case 235: f = (a > d); break; // A>D
				case 236: f = (a > U32(m(b))); break; // A>*B
				case 237: f = (a > U32(m(c))); break; // A>*C
				case 238: f = (a > h(d)); break; // A>*D
				case 239: f = (a > U32(header[pc++])); break; // A> N
				case 255: if ((pc = hbegin + header[pc] + 256 * header[pc + 1]) >= hend) err(); break;//LJ
				default: err();
			}
			return 1;
		}

		private void run0(U32 input) // default run() if not JIT
		{
			assert(cend > 6);
			assert(hbegin >= cend + 128);
			assert(hend >= hbegin);
			assert(hend < header.isize() - 130);
			assert(m.size() > 0);
			assert(h.size() > 0);
			assert(header[0] + 256 * header[1] == cend + hend - hbegin - 2);
			pc = hbegin;
			a = input;
			while (execute()) ;
		}

		private void div(U32 x)
		{
			if (x != 0)
			{
				a /= x;
			}
			else
			{
				a = 0;
			}
		}

		private void mod(U32 x)
		{
			if (x != 0)
			{
				a %= x;
			}
			else
			{
				a = 0;
			}
		}

		private void swap(ref U32 x)
		{
			a ^= x;
			x ^= a;
			a ^= x;
		}

		private void swap(ref U8 x)
		{
			a ^= x;
			x = (U8)(x ^ a);
			a ^= x;
		}

		// pow(2, x)
		static double pow2(int x)
		{
			double r = 1;
			for (; x > 0; x--) r += r;
			return r;
		}

		// Print illegal instruction error message and exit
		private void err() // exit with run time error
		{
			LibZPAQ.error("ZPAQL execution error");
		}
	}
}
