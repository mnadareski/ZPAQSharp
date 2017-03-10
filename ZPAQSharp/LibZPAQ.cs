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
	public class LibZPAQ
	{
		// Tables for parsing ZPAQL source code
		public static char[] compname = new char[256];      // list of ZPAQL component types
		public static int[] compsize = new int[256];        // number of bytes to encode a component
		public static char[] opcodelist = new char[272];    // list of ZPAQL instructions

		// Callback for error handling
		public static void error(string msg)
		{
		}

		// Read 16 bit little-endian number
		public static int toU16(string p)
		{
			return (p[0] & 255) + 256 * (p[1] & 255);
		}

		// Put n cryptographic random bytes in buf[0..n-1].
		// The first byte will not be 'z' or '7' (start of a ZPAQ archive).
		// For a pure random number, discard the first byte.
		// In VC++, must link to advapi32.lib.
		public static void random(ref char[] buf, int n)
		{
# ifdef unix
			FILE * in= fopen("/dev/urandom", "rb");
			if (in && int(fread(buf, 1, n, in)) == n)
    fclose(in);
  else {
				error("key generation failed");
			}
#else
			HCRYPTPROV h;
			if (CryptAcquireContext(&h, NULL, NULL, PROV_RSA_FULL,
				CRYPT_VERIFYCONTEXT) && CryptGenRandom(h, n, (BYTE*)buf))
				CryptReleaseContext(h, 0);
			else
			{
				fprintf(stderr, "CryptGenRandom: error %d\n", int(GetLastError()));
				error("key generation failed");
			}
#endif
			if (n >= 1 && (buf[0] == 'z' || buf[0] == '7'))
				buf[0] ^= 0x80;
		}

		// Symbolic constants, instruction size, and names
		public enum CompType
		{
			NONE, CONS, CM, ICM, MATCH, AVG, MIX2, MIX, ISSE, SSE
		}

		static void decompress(Reader @in, Writer @out)
		{
			Decompresser d;
			d.setInput(in);
			d.setOutput(out);
			while (d.findBlock())
			{       // don't calculate memory
				while (d.findFilename())
				{  // discard filename
					d.readComment();          // discard comment
					d.decompress();           // to end of segment
					d.readSegmentEnd();       // discard sha1string
				}
			}
		}

		// Compress in to out in multiple blocks. Default method is "14,128,0"
		// Default filename is "". Comment is appended to input size.
		// dosha1 means save the SHA-1 checksum.
		static void compress(Reader @in, Writer @out, string method, string filename = null, string comment = null, bool dosha1 = true)
		{
			// Get block size
			int bs = 4;
			if (method && method[0] && method[1] >= '0' && method[1] <= '9')
			{
				bs = method[1] - '0';
				if (method[2] >= '0' && method[2] <= '9') bs = bs * 10 + method[2] - '0';
				if (bs > 11) bs = 11;
			}
			bs = (0x100000 << bs) - 4096;

			// Compress in blocks
			StringBuffer sb(bs);
			sb.write(0, bs);
			int n = 0;
			while (in && (n =in->read((char*)sb.data(), bs))> 0) {
				sb.resize(n);
				compressBlock(&sb, out, method, filename, comment, dosha1);
				filename = 0;
				comment = 0;
				sb.resize(0);
			}
		}

		// Same as compress() but output is 1 block, ignoring block size parameter.
		// Compress from in to out in 1 segment in 1 block using the algorithm
		// descried in method. If method begins with a digit then choose
		// a method depending on type. Save filename and comment
		// in the segment header. If comment is 0 then the default is the input size
		// as a decimal string, plus " jDC\x01" for a journaling method (method[0]
		// is not 's'). Write the generated method to methodOut if not 0.
		static void compressBlock(StringBuffer @in, Writer @out, string method, string filename = null, string comment = null, bool dosha1 = true)
		{
			assert(in);
			assert(out);
			assert(method_);
			assert(method_[0]);
			std::string method = method_;
			const unsigned n =in->size();  // input size
			const int arg0 = MAX(lg(n + 4095) - 20, 0);  // block size
			assert((1u << (arg0 + 20)) >= n + 4096);

			// Get type from method "LB,R,t" where L is level 0..5, B is block
			// size 0..11, R is redundancy 0..255, t = 0..3 = binary, text, exe, both.
			unsigned type = 0;
			if (isdigit(method[0]))
			{
				int commas = 0, arg[4] = { 0 };
				for (int i = 1; i < int(method.size()) && commas < 4; ++i)
				{
					if (method[i] == ',' || method[i] == '.') ++commas;
					else if (isdigit(method[i])) arg[commas] = arg[commas] * 10 + method[i] - '0';
				}
				if (commas == 0) type = 512;
				else type = arg[1] * 4 + arg[2];
			}

			// Get hash of input
			libzpaq::SHA1 sha1;
			const char* sha1ptr = 0;
# ifdef DEBUG
			if (true)
			{
#else
				if (dosha1)
				{
#endif
					sha1.write(in->c_str(), n);
					sha1ptr = sha1.result();
				}

				// Expand default methods
				if (isdigit(method[0]))
				{
					const int level = method[0] - '0';
					assert(level >= 0 && level <= 9);

					// build models
					const int doe8 = (type & 2) * 2;
					method = "x" + itos(arg0);
					std::string htsz = "," + itos(19 + arg0 + (arg0 <= 6));  // lz77 hash table size
					std::string sasz = "," + itos(21 + arg0);            // lz77 suffix array size

					// store uncompressed
					if (level == 0)
						method = "0" + itos(arg0) + ",0";

					// LZ77, no model. Store if hard to compress
					else if (level == 1)
					{
						if (type < 40) method += ",0";
						else
						{
							method += "," + itos(1 + doe8) + ",";
							if (type < 80) method += "4,0,1,15";
							else if (type < 128) method += "4,0,2,16";
							else if (type < 256) method += "4,0,2" + htsz;
							else if (type < 960) method += "5,0,3" + htsz;
							else method += "6,0,3" + htsz;
						}
					}

					// LZ77 with longer search
					else if (level == 2)
					{
						if (type < 32) method += ",0";
						else
						{
							method += "," + itos(1 + doe8) + ",";
							if (type < 64) method += "4,0,3" + htsz;
							else method += "4,0,7" + sasz + ",1";
						}
					}

					// LZ77 with CM depending on redundancy
					else if (level == 3)
					{
						if (type < 20)  // store if not compressible
							method += ",0";
						else if (type < 48)  // fast LZ77 if barely compressible
							method += "," + itos(1 + doe8) + ",4,0,3" + htsz;
						else if (type >= 640 || (type & 1))  // BWT if text or highly compressible
							method += "," + itos(3 + doe8) + "ci1";
						else  // LZ77 with O0-1 compression of up to 12 literals
							method += "," + itos(2 + doe8) + ",12,0,7" + sasz + ",1c0,0,511i2";
					}

					// LZ77+CM, fast CM, or BWT depending on type
					else if (level == 4)
					{
						if (type < 12)
							method += ",0";
						else if (type < 24)
							method += "," + itos(1 + doe8) + ",4,0,3" + htsz;
						else if (type < 48)
							method += "," + itos(2 + doe8) + ",5,0,7" + sasz + "1c0,0,511";
						else if (type < 900)
						{
							method += "," + itos(doe8) + "ci1,1,1,1,2a";
							if (type & 1) method += "w";
							method += "m";
						}
						else
							method += "," + itos(3 + doe8) + "ci1";
					}

					// Slow CM with lots of models
					else
					{  // 5..9

						// Model text files
						method += "," + itos(doe8);
						if (type & 1) method += "w2c0,1010,255i1";
						else method += "w1i1";
						method += "c256ci1,1,1,1,1,1,2a";

						// Analyze the data
						const int NR = 1 << 12;
						int pt[256] = { 0 };  // position of last occurrence
						int r[NR] = { 0 };    // count repetition gaps of length r
						const unsigned char* p =in->data();
						if (level > 0)
						{
							for (unsigned i = 0; i < n; ++i)
							{
								const int k = i - pt[p[i]];
								if (k > 0 && k < NR) ++r[k];
								pt[p[i]] = i;
							}
						}

						// Add periodic models
						int n1 = n - r[1] - r[2] - r[3];
						for (int i = 0; i < 2; ++i)
						{
							int period = 0;
							double score = 0;
							int t = 0;
							for (int j = 5; j < NR && t < n1; ++j)
							{
								const double s = r[j] / (256.0 + n1 - t);
								if (s > score) score = s, period = j;
								t += r[j];
							}
							if (period > 4 && score > 0.1)
							{
								method += "c0,0," + itos(999 + period) + ",255i1";
								if (period <= 255)
									method += "c0," + itos(period) + "i1";
								n1 -= r[period];
								r[period] = 0;
							}
							else
								break;
						}
						method += "c0,2,0,255i1c0,3,0,0,255i1c0,4,0,0,0,255i1mm16ts19t0";
					}
				}

				// Compress
				std::string config;
				int args[9] = { 0 };
				config = makeConfig(method.c_str(), args);
				assert(n <= (0x100000u << args[0]) - 4096);
				libzpaq::Compressor co;
				co.setOutput(out);
# ifdef DEBUG
				co.setVerify(true);
#endif
				StringBuffer pcomp_cmd;
				co.writeTag();
				co.startBlock(config.c_str(), args, &pcomp_cmd);
				std::string cs = itos(n);
				if (comment) cs = cs + " " + comment;
				co.startSegment(filename, cs.c_str());
				if (args[1] >= 1 && args[1] <= 7 && args[1] != 4)
				{  // LZ77 or BWT
					LZBuffer lz(*in, args);
					co.setInput(&lz);
					co.compress();
				}
				else
				{  // compress with e8e9 or no preprocessing
					if (args[1] >= 4 && args[1] <= 7)
						e8e9(in->data(), in->size());
					co.setInput(in);
					co.compress();
				}
# ifdef DEBUG  // verify pre-post processing are inverses
				int64_t outsize;
				const char* sha1result = co.endSegmentChecksum(&outsize, dosha1);
				assert(sha1result);
				assert(sha1ptr);
				if (memcmp(sha1result, sha1ptr, 20) != 0)
					error("Pre/post-processor test failed");
#else
				co.endSegment(sha1ptr);
#endif
				co.endBlock();
			}

		// Allocate newsize > 0 bytes of executable memory and update
		// p to point to it and newsize = n. Free any previously
		// allocated memory first. If newsize is 0 then free only.
		// Call error in case of failure. If NOJIT, ignore newsize
		// and set p=0, n=0 without allocating memory.
		void allocx(ref U8[] p, ref int n, int newsize)
		{
			if (p != null || n != 0)
			{
				if (p)
				{
					munmap(p, n);
				}
				p = null;
				n = 0;
			}
			if (newsize > 0)
			{
				p = (U8*)mmap(0, newsize, PROT_READ | PROT_WRITE | PROT_EXEC,
							MAP_PRIVATE | MAP_ANON, -1, 0);
				if ((void*)p == MAP_FAILED)
					p = null;
				if (p)
					n = newsize;
				else
				{
					n = 0;
					error("allocx failed");
				}
			}
		}

		// Convert non-negative decimal number x to string of at least n digits
		std::string itos(int64_t x, int n = 1)
		{
			assert(x >= 0);
			assert(n >= 0);
			std::string r;
			for (; x || n > 0; x /= 10, --n) r = std::string(1, '0' + x % 10) + r;
			return r;
		}

		// E8E9 transform of buf[0..n-1] to improve compression of .exe and .dll.
		// Patterns (E8|E9 xx xx xx 00|FF) at offset i replace the 3 middle
		// bytes with x+i mod 2^24, LSB first, reading backward.
		void e8e9(unsigned char* buf, int n)
		{
			for (int i = n - 5; i >= 0; --i)
			{
				if (((buf[i] & 254) == 0xe8) && ((buf[i + 4] + 1) & 254) == 0)
				{
					unsigned a = (buf[i + 1] | buf[i + 2] << 8 | buf[i + 3] << 16) + i;
					buf[i + 1] = a;
					buf[i + 2] = a >> 8;
					buf[i + 3] = a >> 16;
				}
			}
		}

		// Generate a config file from the method argument with syntax:
		// {0|x|s|i}[N1[,N2]...][{ciamtswf<cfg>}[N1[,N2]]...]...
		std::string makeConfig(const char* method, int args[]) {
  assert(method);
		const char type = method[0];
  assert(type=='x' || type=='s' || type=='0' || type=='i');

		// Read "{x|s|i|0}N1,N2...N9" into args[0..8] ($1..$9)
		args[0]=0;  // log block size in MiB
  args[1]=0;  // 0=none, 1=var-LZ77, 2=byte-LZ77, 3=BWT, 4..7 adds E8E9
  args[2]=0;  // lz77 minimum match length
  args[3]=0;  // secondary context length
  args[4]=0;  // log searches
  args[5]=0;  // lz77 hash table size or SA if args[0]+21
  args[6]=0;  // secondary context look ahead
  args[7]=0;  // not used
  args[8]=0;  // not used
  if (isdigit(*++method)) args[0]=0;
  for (int i=0; i<9 && (isdigit(* method) || *method==',' || *method=='.');) {
    if (isdigit(* method))
      args[i]=args[i]*10+*method-'0';
    else if (++i<9)

	  args[i]=0;
    ++method;
  }

  // "0..." = No compression
  if (type=='0')
    return "comp 0 0 0 0 0 hcomp end\n";

  // Generate the postprocessor
  std::string hdr, pcomp;
	const int level = args[1] & 3;
	const bool doe8 = args[1] >= 4 && args[1] <= 7;

  // LZ77+Huffman, with or without E8E9
  if (level==1) {
    const int rb = args[0] > 4 ? args[0] - 4 : 0;
	hdr="comp 9 16 0 $1+20 ";
    pcomp=
    "pcomp lazy2 3 ;\n"
    " (r1 = state\n"
    "  r2 = len - match or literal length\n"
    "  r3 = m - number of offset bits expected\n"
    "  r4 = ptr to buf\n"
    "  r5 = r - low bits of offset\n"
    "  c = bits - input buffer\n"
    "  d = n - number of bits in c)\n"
    "\n"
    "  a> 255 if\n";
    if (doe8)
	  pcomp+=
      "    b=0 d=r 4 do (for b=0..d-1, d = end of buf)\n"
      "      a=b a==d ifnot\n"
      "        a+= 4 a<d if\n"
      "          a=*b a&= 254 a== 232 if (e8 or e9?)\n"
      "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if (00 or ff)\n"
      "              b-- a=*b\n"
      "              b-- a<<= 8 a+=*b\n"
      "              b-- a<<= 8 a+=*b\n"
      "              a-=b a++\n"
      "              *b=a a>>= 8 b++\n"
      "              *b=a a>>= 8 b++\n"
      "              *b=a b++\n"
      "            endif\n"
      "            b=c\n"
      "          endif\n"
      "        endif\n"
      "        a=*b out b++\n"
      "      forever\n"
      "    endif\n"
      "\n";
    pcomp+=
    "    (reset state)\n"
    "    a=0 b=0 c=0 d=0 r=a 1 r=a 2 r=a 3 r=a 4\n"
    "    halt\n"
    "  endif\n"
    "\n"
    "  a<<=d a+=c c=a               (bits+=a<<n)\n"
    "  a= 8 a+=d d=a                (n+=8)\n"
    "\n"
    "  (if state==0 (expect new code))\n"
    "  a=r 1 a== 0 if (match code mm,mmm)\n"
    "    a= 1 r=a 2                 (len=1)\n"
    "    a=c a&= 3 a> 0 if          (if (bits&3))\n"
    "      a-- a<<= 3 r=a 3           (m=((bits&3)-1)*8)\n"
    "      a=c a>>= 2 c=a             (bits>>=2)\n"
    "      b=r 3 a&= 7 a+=b r=a 3     (m+=bits&7)\n"
    "      a=c a>>= 3 c=a             (bits>>=3)\n"
    "      a=d a-= 5 d=a              (n-=5)\n"
    "      a= 1 r=a 1                 (state=1)\n"
    "    else (literal, discard 00)\n"
    "      a=c a>>= 2 c=a             (bits>>=2)\n"
    "      d-- d--                    (n-=2)\n"
    "      a= 3 r=a 1                 (state=3)\n"
    "    endif\n"
    "  endif\n"
    "\n"
    "  (while state==1 && n>=3 (expect match length n*4+ll -> r2))\n"
    "  do a=r 1 a== 1 if a=d a> 2 if\n"
    "    a=c a&= 1 a== 1 if         (if bits&1)\n"
    "      a=c a>>= 1 c=a             (bits>>=1)\n"
    "      b=r 2 a=c a&= 1 a+=b a+=b r=a 2 (len+=len+(bits&1))\n"
    "      a=c a>>= 1 c=a             (bits>>=1)\n"
    "      d-- d--                    (n-=2)\n"
    "    else\n"
    "      a=c a>>= 1 c=a             (bits>>=1)\n"
    "      a=r 2 a<<= 2 b=a           (len<<=2)\n"
    "      a=c a&= 3 a+=b r=a 2       (len+=bits&3)\n"
    "      a=c a>>= 2 c=a             (bits>>=2)\n"
    "      d-- d-- d--                (n-=3)\n";
    if (rb)
	  pcomp+="      a= 5 r=a 1                 (state=5)\n";
    else
      pcomp+="      a= 2 r=a 1                 (state=2)\n";
    pcomp+=
    "    endif\n"
    "  forever endif endif\n"
    "\n";
    if (rb) pcomp+=  // save r in r5
      "  (if state==5 && n>=8) (expect low bits of offset to put in r5)\n"
      "  a=r 1 a== 5 if a=d a> "+itos(rb-1)+" if\n"
      "    a=c a&= "+itos((1<<rb)-1)+" r=a 5            (save r in r5)\n"
      "    a=c a>>= "+itos(rb)+" c=a\n"
      "    a=d a-= "+itos(rb)+ " d=a\n"
      "    a= 2 r=a 1                   (go to state 2)\n"
      "  endif endif\n"
      "\n";
    pcomp+=
    "  (if state==2 && n>=m) (expect m offset bits)\n"
    "  a=r 1 a== 2 if a=r 3 a>d ifnot\n"
    "    a=c r=a 6 a=d r=a 7          (save c=bits, d=n in r6,r7)\n"
    "    b=r 3 a= 1 a<<=b d=a         (d=1<<m)\n"
    "    a-- a&=c a+=d                (d=offset=bits&((1<<m)-1)|(1<<m))\n";
    if (rb)
	  pcomp+=  // insert r into low bits of d
      "    a<<= "+itos(rb)+" d=r 5 a+=d a-= "+itos((1<<rb)-1)+"\n";
    pcomp+=
    "    d=a b=r 4 a=b a-=d c=a       (c=p=(b=ptr)-offset)\n"
    "\n"
    "    (while len-- (copy and output match d bytes from *c to *b))\n"
    "    d=r 2 do a=d a> 0 if d--\n"
    "      a=*c *b=a c++ b++          (buf[ptr++]-buf[p++])\n";
    if (!doe8) pcomp+=" out\n";
    pcomp+=
    "    forever endif\n"
    "    a=b r=a 4\n"
    "\n"
    "    a=r 6 b=r 3 a>>=b c=a        (bits>>=m)\n"
    "    a=r 7 a-=b d=a               (n-=m)\n"
    "    a=0 r=a 1                    (state=0)\n"
    "  endif endif\n"
    "\n"
    "  (while state==3 && n>=2 (expect literal length))\n"
    "  do a=r 1 a== 3 if a=d a> 1 if\n"
    "    a=c a&= 1 a== 1 if         (if bits&1)\n"
    "      a=c a>>= 1 c=a              (bits>>=1)\n"
    "      b=r 2 a&= 1 a+=b a+=b r=a 2 (len+=len+(bits&1))\n"
    "      a=c a>>= 1 c=a              (bits>>=1)\n"
    "      d-- d--                     (n-=2)\n"
    "    else\n"
    "      a=c a>>= 1 c=a              (bits>>=1)\n"
    "      d--                         (--n)\n"
    "      a= 4 r=a 1                  (state=4)\n"
    "    endif\n"
    "  forever endif endif\n"
    "\n"
    "  (if state==4 && n>=8 (expect len literals))\n"
    "  a=r 1 a== 4 if a=d a> 7 if\n"
    "    b=r 4 a=c *b=a\n";
    if (!doe8) pcomp+=" out\n";
    pcomp+=
    "    b++ a=b r=a 4                 (buf[ptr++]=bits)\n"
    "    a=c a>>= 8 c=a                (bits>>=8)\n"
    "    a=d a-= 8 d=a                 (n-=8)\n"
    "    a=r 2 a-- r=a 2 a== 0 if      (if --len<1)\n"
    "      a=0 r=a 1                     (state=0)\n"
    "    endif\n"
    "  endif endif\n"
    "  halt\n"
    "end\n";
  }

  // Byte aligned LZ77, with or without E8E9
  else if (level==2) {
    hdr="comp 9 16 0 $1+20 ";
    pcomp=
    "pcomp lzpre c ;\n"
    "  (Decode LZ77: d=state, M=output buffer, b=size)\n"
    "  a> 255 if (at EOF decode e8e9 and output)\n";
    if (doe8)
	  pcomp+=
      "    d=b b=0 do (for b=0..d-1, d = end of buf)\n"
      "      a=b a==d ifnot\n"
      "        a+= 4 a<d if\n"
      "          a=*b a&= 254 a== 232 if (e8 or e9?)\n"
      "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if (00 or ff)\n"
      "              b-- a=*b\n"
      "              b-- a<<= 8 a+=*b\n"
      "              b-- a<<= 8 a+=*b\n"
      "              a-=b a++\n"
      "              *b=a a>>= 8 b++\n"
      "              *b=a a>>= 8 b++\n"
      "              *b=a b++\n"
      "            endif\n"
      "            b=c\n"
      "          endif\n"
      "        endif\n"
      "        a=*b out b++\n"
      "      forever\n"
      "    endif\n";
    pcomp+=
    "    b=0 c=0 d=0 a=0 r=a 1 r=a 2 (reset state)\n"
    "  halt\n"
    "  endif\n"
    "\n"
    "  (in state d==0, expect a new code)\n"
    "  (put length in r1 and inital part of offset in r2)\n"
    "  c=a a=d a== 0 if\n"
    "    a=c a>>= 6 a++ d=a\n"
    "    a== 1 if (literal?)\n"
    "      a+=c r=a 1 a=0 r=a 2\n"
    "    else (3 to 5 byte match)\n"
    "      d++ a=c a&= 63 a+= $3 r=a 1 a=0 r=a 2\n"
    "    endif\n"
    "  else\n"
    "    a== 1 if (writing literal)\n"
    "      a=c *b=a b++\n";
    if (!doe8) pcomp+=" out\n";
    pcomp+=
    "      a=r 1 a-- a== 0 if d=0 endif r=a 1 (if (--len==0) state=0)\n"
    "    else\n"
    "      a> 2 if (reading offset)\n"
    "        a=r 2 a<<= 8 a|=c r=a 2 d-- (off=off<<8|c, --state)\n"
    "      else (state==2, write match)\n"
    "        a=r 2 a<<= 8 a|=c c=a a=b a-=c a-- c=a (c=i-off-1)\n"
    "        d=r 1 (d=len)\n"
    "        do (copy and output d=len bytes)\n"
    "          a=*c *b=a c++ b++\n";
    if (!doe8) pcomp+=" out\n";
    pcomp+=
    "        d-- a=d a> 0 while\n"
    "        (d=state=0. off, len don\'t matter)\n"
    "      endif\n"
    "    endif\n"
    "  endif\n"
    "  halt\n"
    "end\n";
  }

  // BWT with or without E8E9
  else if (level==3) {  // IBWT
    hdr="comp 9 16 $1+20 $1+20 ";  // 2^$1 = block size in MB
    pcomp=
    "pcomp bwtrle c ;\n"
    "\n"
    "  (read BWT, index into M, size in b)\n"
    "  a> 255 ifnot\n"
    "    *b=a b++\n"
    "\n"
    "  (inverse BWT)\n"
    "  elsel\n"
    "\n"
    "    (index in last 4 bytes, put in c and R1)\n"
    "    b-- a=*b\n"
    "    b-- a<<= 8 a+=*b\n"
    "    b-- a<<= 8 a+=*b\n"
    "    b-- a<<= 8 a+=*b c=a r=a 1\n"
    "\n"
    "    (save size in R2)\n"
    "    a=b r=a 2\n"
    "\n"
    "    (count bytes in H[~1..~255, ~0])\n"
    "    do\n"
    "      a=b a> 0 if\n"
    "        b-- a=*b a++ a&= 255 d=a d! *d++\n"
    "      forever\n"
    "    endif\n"
    "\n"
    "    (cumulative counts: H[~i=0..255] = count of bytes before i)\n"
    "    d=0 d! *d= 1 a=0\n"
    "    do\n"
    "      a+=*d *d=a d--\n"
    "    d<>a a! a> 255 a! d<>a until\n"
    "\n"
    "    (build first part of linked list in H[0..idx-1])\n"
    "    b=0 do\n"
    "      a=c a>b if\n"
    "        d=*b d! *d++ d=*d d-- *d=b\n"
    "      b++ forever\n"
    "    endif\n"
    "\n"
    "    (rest of list in H[idx+1..n-1])\n"
    "    b=c b++ c=r 2 do\n"
    "      a=c a>b if\n"
    "        d=*b d! *d++ d=*d d-- *d=b\n"
    "      b++ forever\n"
    "    endif\n"
    "\n";
    if (args[0]<=4) {  // faster IBWT list traversal limited to 16 MB blocks
      pcomp+=
      "    (copy M to low 8 bits of H to reduce cache misses in next loop)\n"
      "    b=0 do\n"
      "      a=c a>b if\n"
      "        d=b a=*d a<<= 8 a+=*b *d=a\n"
      "      b++ forever\n"
      "    endif\n"
      "\n"
      "    (traverse list and output or copy to M)\n"
      "    d=r 1 b=0 do\n"
      "      a=d a== 0 ifnot\n"
      "        a=*d a>>= 8 d=a\n";
      if (doe8) pcomp+=" *b=*d b++\n";
      else      pcomp+=" a=*d out\n";
      pcomp+=
      "      forever\n"
      "    endif\n"
      "\n";
      if (doe8)  // IBWT+E8E9
		pcomp+=
        "    (e8e9 transform to out)\n"
        "    d=b b=0 do (for b=0..d-1, d = end of buf)\n"
        "      a=b a==d ifnot\n"
        "        a+= 4 a<d if\n"
        "          a=*b a&= 254 a== 232 if\n"
        "            c=b b++ b++ b++ b++ a=*b a++ a&= 254 a== 0 if\n"
        "              b-- a=*b\n"
        "              b-- a<<= 8 a+=*b\n"
        "              b-- a<<= 8 a+=*b\n"
        "              a-=b a++\n"
        "              *b=a a>>= 8 b++\n"
        "              *b=a a>>= 8 b++\n"
        "              *b=a b++\n"
        "            endif\n"
        "            b=c\n"
        "          endif\n"
        "        endif\n"
        "        a=*b out b++\n"
        "      forever\n"
        "    endif\n";
      pcomp+=
      "  endif\n"
      "  halt\n"
      "end\n";
    }
    else {  // slower IBWT list traversal for all sized blocks
      if (doe8) {  // E8E9 after IBWT
        pcomp+=
        "    (R2 = output size without EOS)\n"
        "    a=r 2 a-- r=a 2\n"
        "\n"
        "    (traverse list (d = IBWT pointer) and output inverse e8e9)\n"
        "    (C = offset = 0..R2-1)\n"
        "    (R4 = last 4 bytes shifted in from MSB end)\n"
        "    (R5 = temp pending output byte)\n"
        "    c=0 d=r 1 do\n"
        "      a=d a== 0 ifnot\n"
        "        d=*d\n"
        "\n"
        "        (store byte in R4 and shift out to R5)\n"
        "        b=d a=*b a<<= 24 b=a\n"
        "        a=r 4 r=a 5 a>>= 8 a|=b r=a 4\n"
        "\n"
        "        (if E8|E9 xx xx xx 00|FF in R4:R5 then subtract c from x)\n"
        "        a=c a> 3 if\n"
        "          a=r 5 a&= 254 a== 232 if\n"
        "            a=r 4 a>>= 24 b=a a++ a&= 254 a< 2 if\n"
        "              a=r 4 a-=c a+= 4 a<<= 8 a>>= 8 \n"
        "              b<>a a<<= 24 a+=b r=a 4\n"
        "            endif\n"
        "          endif\n"
        "        endif\n"
        "\n"
        "        (output buffered byte)\n"
        "        a=c a> 3 if a=r 5 out endif c++\n"
        "\n"
        "      forever\n"
        "    endif\n"
        "\n"
        "    (output up to 4 pending bytes in R4)\n"
        "    b=r 4\n"
        "    a=c a> 3 a=b if out endif a>>= 8 b=a\n"
        "    a=c a> 2 a=b if out endif a>>= 8 b=a\n"
        "    a=c a> 1 a=b if out endif a>>= 8 b=a\n"
        "    a=c a> 0 a=b if out endif\n"
        "\n"
        "  endif\n"
        "  halt\n"
        "end\n";
      }
      else {
        pcomp+=
        "    (traverse list and output)\n"
        "    d=r 1 do\n"
        "      a=d a== 0 ifnot\n"
        "        d=*d\n"
        "        b=d a=*b out\n"
        "      forever\n"
        "    endif\n"
        "  endif\n"
        "  halt\n"
        "end\n";
      }
    }
  }

  // E8E9 or no preprocessing
  else if (level==0) {
    hdr="comp 9 16 0 0 ";
    if (doe8) { // E8E9?
      pcomp=
      "pcomp e8e9 d ;\n"
      "  a> 255 if\n"
      "    a=c a> 4 if\n"
      "      c= 4\n"
      "    else\n"
      "      a! a+= 5 a<<= 3 d=a a=b a>>=d b=a\n"
      "    endif\n"
      "    do a=c a> 0 if\n"
      "      a=b out a>>= 8 b=a c--\n"
      "    forever endif\n"
      "  else\n"
      "    *b=b a<<= 24 d=a a=b a>>= 8 a+=d b=a c++\n"
      "    a=c a> 4 if\n"
      "      a=*b out\n"
      "      a&= 254 a== 232 if\n"
      "        a=b a>>= 24 a++ a&= 254 a== 0 if\n"
      "          a=b a>>= 24 a<<= 24 d=a\n"
      "          a=b a-=c a+= 5\n"
      "          a<<= 8 a>>= 8 a|=d b=a\n"
      "        endif\n"
      "      endif\n"
      "    endif\n"
      "  endif\n"
      "  halt\n"
      "end\n";
    }
    else
      pcomp="end\n";
  }
  else

	error("Unsupported method");

// Build context model (comp, hcomp) assuming:
// H[0..254] = contexts
// H[255..511] = location of last byte i-255
// M = last 64K bytes, filling backward
// C = pointer to most recent byte
// R1 = level 2 lz77 1+bytes expected until next code, 0=init
// R2 = level 2 lz77 first byte of code
int ncomp = 0;  // number of components
const int membits = args[0] + 20;
int sb = 5;  // bits in last context
std::string comp;
std::string hcomp = "hcomp\n"
    "c-- *c=a a+= 255 d=a *d=c\n";
  if (level==2) {  // put level 2 lz77 parse state in R1, R2
    hcomp+=
    "  (decode lz77 into M. Codes:\n"
    "  00xxxxxx = literal length xxxxxx+1\n"
    "  xx......, xx > 0 = match with xx offset bytes to follow)\n"
    "\n"
    "  a=r 1 a== 0 if (init)\n"
    "    a= "+itos(111+57*doe8)+" (skip post code)\n"
    "  else a== 1 if  (new code?)\n"
    "    a=*c r=a 2  (save code in R2)\n"
    "    a> 63 if a>>= 6 a++ a++  (match)\n"
    "    else a++ a++ endif  (literal)\n"
    "  else (read rest of code)\n"
    "    a--\n"
    "  endif endif\n"
    "  r=a 1  (R1 = 1+expected bytes to next code)\n";
  }

  // Generate the context model
  while (* method && ncomp<254) {

    // parse command C[N1[,N2]...] into v = {C, N1, N2...}
    std::vector<int> v;
v.push_back(* method++);
    if (isdigit(* method)) {
      v.push_back(* method++-'0');
      while (isdigit(* method) || * method==',' || * method=='.') {
        if (isdigit(* method))
          v.back()=v.back() *10+* method++-'0';
        else {
          v.push_back(0);
          ++method;
        }
      }
    }

    // c: context model
    // N1%1000: 0=ICM 1..256=CM limit N1-1
    // N1/1000: number of times to halve memory
    // N2: 1..255=offset mod N2. 1000..1255=distance to N2-1000
    // N3...: 0..255=byte mask + 256=lz77 state. 1000+=run of N3-1000 zeros.
    if (v[0]=='c') {
      while (v.size()<3) v.push_back(0);
      comp+=itos(ncomp)+" ";
      sb=11;  // count context bits
      if (v[2]<256) sb+=lg(v[2]);
      else sb+=6;
      for (unsigned i=3; i<v.size(); ++i)
        if (v[i]<512) sb+=nbits(v[i])*3/4;
      if (sb>membits) sb=membits;
      if (v[1]%1000==0) comp+="icm "+itos(sb-6-v[1]/1000)+"\n";
      else comp+="cm "+itos(sb-2-v[1]/1000)+" "+itos(v[1]%1000-1)+"\n";

      // special contexts
      hcomp+="d= "+itos(ncomp)+" *d=0\n";
      if (v[2]>1 && v[2]<=255) {  // periodic context
        if (lg(v[2])!=lg(v[2]-1))
          hcomp+="a=c a&= "+itos(v[2]-1)+" hashd\n";
        else
          hcomp+="a=c a%= "+itos(v[2])+" hashd\n";
      }
      else if (v[2]>=1000 && v[2]<=1255)  // distance context
        hcomp+="a= 255 a+= "+itos(v[2]-1000)+
               " d=a a=*d a-=c a> 255 if a= 255 endif d= "+

			   itos(ncomp)+" hashd\n";

      // Masked context
      for (unsigned i=3; i<v.size(); ++i) {
        if (i==3) hcomp+="b=c ";
        if (v[i]==255)
          hcomp+="a=*b hashd\n";  // ordinary byte
        else if (v[i]>0 && v[i]<255)
          hcomp+="a=*b a&= "+itos(v[i])+" hashd\n";  // masked byte
        else if (v[i]>=256 && v[i]<512) { // lz77 state or masked literal byte
          hcomp+=
          "a=r 1 a> 1 if\n"  // expect literal or offset
          "  a=r 2 a< 64 if\n"  // expect literal
          "    a=*b ";
          if (v[i]<511) hcomp+="a&= "+itos(v[i]-256);
hcomp+=" hashd\n"
          "  else\n"  // expect match offset byte
          "    a>>= 6 hashd a=r 1 hashd\n"
          "  endif\n"
          "else\n"  // expect new code
          "  a= 255 hashd a=r 2 hashd\n"
          "endif\n";
        }
        else if (v[i]>=1256)  // skip v[i]-1000 bytes
          hcomp+="a= "+itos(((v[i]-1000)>>8)&255)+" a<<= 8 a+= "
               +itos((v[i]-1000)&255)+
          " a+=b b=a\n";
        else if (v[i]>1000)
          hcomp+="a= "+itos(v[i]-1000)+" a+=b b=a\n";
        if (v[i]<512 && i<v.size()-1)
          hcomp+="b++ ";
      }
      ++ncomp;
    }

    // m,8,24: MIX, size, rate
    // t,8,24: MIX2, size, rate
    // s,8,32,255: SSE, size, start, limit
    if (strchr("mts", v[0]) && ncomp>int (v[0]=='t')) {
      if (v.size()<=1) v.push_back(8);
      if (v.size()<=2) v.push_back(24+8* (v[0]=='s'));
      if (v[0]=='s' && v.size()<=3) v.push_back(255);
      comp+=itos(ncomp);
sb=5+v[1]*3/4;
      if (v[0]=='m')
        comp+=" mix "+itos(v[1])+" 0 "+itos(ncomp)+" "+itos(v[2])+" 255\n";
      else if (v[0]=='t')
        comp+=" mix2 "+itos(v[1])+" "+itos(ncomp-1)+" "+itos(ncomp-2)
            +" "+itos(v[2])+" 255\n";
      else // s
        comp+=" sse "+itos(v[1])+" "+itos(ncomp-1)+" "+itos(v[2])+" "
            +itos(v[3])+"\n";
      if (v[1]>8) {
        hcomp+="d= "+itos(ncomp)+" *d=0 b=c a=0\n";
        for (; v[1]>=16; v[1]-=8) {
          hcomp+="a<<= 8 a+=*b";
          if (v[1]>16) hcomp+=" b++";
          hcomp+="\n";
        }
        if (v[1]>8)
          hcomp+="a<<= 8 a+=*b a>>= "+itos(16-v[1])+"\n";
        hcomp+="a<<= 8 *d=a\n";
      }
      ++ncomp;
    }

    // i: ISSE chain with order increasing by N1,N2...
    if (v[0]=='i' && ncomp>0) {

	  assert(sb>=5);
hcomp+="d= "+itos(ncomp-1)+" b=c a=*d d++\n";
      for (unsigned i=1; i<v.size() && ncomp<254; ++i) {
        for (int j=0; j<v[i]%10; ++j) {
          hcomp+="hash ";
          if (i<v.size()-1 || j<v[i]%10-1) hcomp+="b++ ";
          sb+=6;
        }
        hcomp+="*d=a";
        if (i<v.size()-1) hcomp+=" d++";
        hcomp+="\n";
        if (sb>membits) sb=membits;
        comp+=itos(ncomp)+" isse "+itos(sb-6-v[i]/10)+" "+itos(ncomp-1)+"\n";
        ++ncomp;
      }
    }

    // a24,0,0: MATCH. N1=hash multiplier. N2,N3=halve buf, table.
    if (v[0]=='a') {
      if (v.size()<=1) v.push_back(24);
      while (v.size()<4) v.push_back(0);
      comp+=itos(ncomp)+" match "+itos(membits-v[3]-2)+" "
          +itos(membits-v[2])+"\n";
      hcomp+="d= "+itos(ncomp)+" a=*d a*= "+itos(v[1])
           +" a+=*c a++ *d=a\n";
      sb=5+(membits-v[2])*3/4;
      ++ncomp;
    }

    // w1,65,26,223,20,0: ICM-ISSE chain of length N1 with word contexts,
    // where a word is a sequence of c such that c&N4 is in N2..N2+N3-1.
    // Word is hashed by: hash := hash*N5+c+1
    // Decrease memory by 2^-N6.
    if (v[0]=='w') {
      if (v.size()<=1) v.push_back(1);
      if (v.size()<=2) v.push_back(65);
      if (v.size()<=3) v.push_back(26);
      if (v.size()<=4) v.push_back(223);
      if (v.size()<=5) v.push_back(20);
      if (v.size()<=6) v.push_back(0);
      comp+=itos(ncomp)+" icm "+itos(membits-6-v[6])+"\n";
      for (int i=1; i<v[1]; ++i)
        comp+=itos(ncomp+i)+" isse "+itos(membits-6-v[6])+" "
            +itos(ncomp+i-1)+"\n";
      hcomp+="a=*c a&= "+itos(v[4])+" a-= "+itos(v[2])+" a&= 255 a< "
           +itos(v[3])+" if\n";
      for (int i=0; i<v[1]; ++i) {
        if (i==0) hcomp+="  d= "+itos(ncomp);
        else hcomp+="  d++";
        hcomp+=" a=*d a*= "+itos(v[5])+" a+=*c a++ *d=a\n";
      }
      hcomp+="else\n";
      for (int i=v[1]-1; i>0; --i)
        hcomp+="  d= "+itos(ncomp+i-1)+" a=*d d++ *d=a\n";
      hcomp+="  d= "+itos(ncomp)+" *d=0\n"
           "endif\n";
      ncomp+=v[1]-1;
      sb=membits-v[6];
      ++ncomp;
    }
  }
  return hdr+itos(ncomp)+"\n"+comp+hcomp+"halt\n"+pcomp;
}
	}
}
