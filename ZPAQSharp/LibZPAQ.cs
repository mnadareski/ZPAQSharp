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
		}

		// Compress in to out in multiple blocks. Default method is "14,128,0"
		// Default filename is "". Comment is appended to input size.
		// dosha1 means save the SHA-1 checksum.
		static void compress(Reader @in, Writer @out, string method, string filename = null, string comment = null, bool dosha1 = true)
		{
		}

		// Same as compress() but output is 1 block, ignoring block size parameter.
		static void compressBlock(StringBuffer @in, Writer @out, string method, string filename = null, string comment = null, bool dosha1 = true)
		{
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
	}
}
