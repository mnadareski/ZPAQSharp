using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZPAQSharp
{
	class Compressor
	{
		public Compressor()
		{
			enc = new Encoder(z);
			@in = null;
			state = State.INIT;
			verify = false;
		}

		public void ssetOutput(Writer @out)
		{
			enc.@out = @out;
		}

		// Write 13 byte start tag
		// "\x37\x6B\x53\x74\xA0\x31\x83\xD3\x8C\xB2\x28\xB0\xD3"
		public void writeTag()
		{
			assert(state == INIT);
			enc.@out.put(0x37);
			enc.@out.put(0x6b);
			enc.@out.put(0x53);
			enc.@out.put(0x74);
			enc.@out.put(0xa0);
			enc.@out.put(0x31);
			enc.@out.put(0x83);
			enc.@out.put(0xd3);
			enc.@out.put(0x8c);
			enc.@out.put(0xb2);
			enc.@out.put(0x28);
			enc.@out.put(0xb0);
			enc.@out.put(0xd3);
		}

		public void startBlock(int level) // level=1,2,3
		{
			// Model 1 - min.cfg
			static const char models[] ={
			  26,0,1,2,0,0,2,3,16,8,19,0,0,96,4,28,
			  59,10,59,112,25,10,59,10,59,112,56,0,

			  // Model 2 - mid.cfg
			  69,0,3,3,0,0,8,3,5,8,13,0,8,17,1,8,
			  18,2,8,18,3,8,19,4,4,22,24,7,16,0,7,24,
			  (char)-1,0,17,104,74,4,95,1,59,112,10,25,59,112,10,25,
			  59,112,10,25,59,112,10,25,59,112,10,25,59,10,59,112,
			  25,69,(char)-49,8,112,56,0,

			  // Model 3 - max.cfg
			  (char)-60,0,5,9,0,0,22,1,(char)-96,3,5,8,13,1,8,16,
			  2,8,18,3,8,19,4,8,19,5,8,20,6,4,22,24,
			  3,17,8,19,9,3,13,3,13,3,13,3,14,7,16,0,
			  15,24,(char)-1,7,8,0,16,10,(char)-1,6,0,15,16,24,0,9,
			  8,17,32,(char)-1,6,8,17,18,16,(char)-1,9,16,19,32,(char)-1,6,
			  0,19,20,16,0,0,17,104,74,4,95,2,59,112,10,25,
			  59,112,10,25,59,112,10,25,59,112,10,25,59,112,10,25,
			  59,10,59,112,10,25,59,112,10,25,69,(char)-73,32,(char)-17,64,47,
			  14,(char)-25,91,47,10,25,60,26,48,(char)-122,(char)-105,20,112,63,9,70,
			  (char)-33,0,39,3,25,112,26,52,25,25,74,10,4,59,112,25,
			  10,4,59,112,25,10,4,59,112,25,65,(char)-113,(char)-44,72,4,59,
			  112,8,(char)-113,(char)-40,8,68,(char)-81,60,60,25,69,(char)-49,9,112,25,25,
			  25,25,25,112,56,0,

			  0,0}; // 0,0 = end of list

			if (level < 1) error("compression level must be at least 1");
			const char* p = models;
			int i;
			for (i = 1; i < level && toushort(p); ++i)
				p += toushort(p) + 2;
			if (toushort(p) < 1) error("compression level too high");
			startBlock(p);
		}

		public void startBlock(char[] hcomp) // ZPAQL byte code
		{
			assert(state == INIT);
			MemoryReader m = new MemoryReader(hcomp, 0);
			z.read(&m);
			pz.sha1 = &sha1;
			assert(z.header.Length > 6);
			enc.@out.put('z');
			enc.@out.put('P');
			enc.@out.put('Q');
			enc.@out.put(1 + (z.header[6] == 0));  // level 1 or 2
			enc.@out.put(1);
			z.write(enc.@out, false);
			state = BLOCK1;
		}

		public void startBlock(char[] config, // ZPAQL source code
			int[] args, // NULL or int[9] arguments
			Writer pcomp_cmd = null) // retrieve preprocessor command
		{
			assert(state == INIT);
			Compiler(config, args, z, pz, pcomp_cmd);
			pz.sha1 = &sha1;
			assert(z.header.Length > 6);
			enc.@out.put('z');
			enc.@out.put('P');
			enc.@out.put('Q');
			enc.@out.put(1 + (z.header[6] == 0));  // level 1 or 2
			enc.@out.put(1);
			z.write(enc.@out, false);
			state = BLOCK1;
		}

		public void setVerify(bool v) // check postprocessing?
		{
			verify = v;
		}

		public void hcomp(Writer out2)
		{
			z.write(out2, false);
		}

		public bool pcomp(Writer out2)
		{
			return pz.write(out2, true);
		}

		public void startSegment(char[] filename = null, char[] comment = null)
		{
			assert(state == BLOCK1 || state == BLOCK2);
			enc.@out.put(1);
			while (filename && *filename)
				enc.@out.put(*filename++);
			enc.@out.put(0);
			while (comment && *comment)
				enc.@out.put(*comment++);
			enc.@out.put(0);
			enc.@out.put(0);
			if (state == BLOCK1) state = SEG1;
			if (state == BLOCK2) state = SEG2;
		}

		public void setInput(Reader i)
		{
			@in = i;
		}

		// Initialize encoding and write pcomp to first segment
		// If len is 0 then length is encoded in pcomp[0..1]
		// if pcomp is 0 then get pcomp from pz.header
		public void postProcess(string pcomp = null, string comment = null) // byte code
		{
			if (state == SEG2) return;
			assert(state == SEG1);
			enc.@init();
			if (!pcomp)
			{
				len = pz.hend - pz.hbegin;
				if (len > 0)
				{
					assert(pz.header.Length > pz.hend);
					assert(pz.hbegin >= 0);
					pcomp = (const char*)&pz.header[pz.hbegin];
				}
				assert(len >= 0);
			}
			else if (len == 0)
			{
				len = toushort(pcomp);
				pcomp += 2;
			}
			if (len > 0)
			{
				enc.compress(1);
				enc.compress(len & 255);
				enc.compress((len >> 8) & 255);
				for (int i = 0; i < len; ++i)
					enc.compress(pcomp[i] & 255);
				if (verify)
					pz.@initp();
			}
			else
				enc.compress(0);
			state = SEG2;
		}

		// Compress n bytes, or to EOF if n < 0
		public bool compress(int n = -1) // n bytes, -1=all, return true until done
		{
			if (state == SEG1)
				postProcess();
			assert(state == SEG2);

			const int BUFSIZE = 1 << 14;
			char buf[BUFSIZE];  // input buffer
			while (n)
			{
				int nbuf = BUFSIZE;  // bytes read into buf
				if (n >= 0 && n < nbuf) nbuf = n;
				int nr =in.read(buf, nbuf);
				if (nr < 0 || nr > BUFSIZE || nr > nbuf) error("invalid read size");
				if (nr <= 0) return false;
				if (n >= 0) n -= nr;
				for (int i = 0; i < nr; ++i)
				{
					int ch = byte(buf[i]);
					enc.compress(ch);
					if (verify)
					{
						if (pz.hend) pz.run(ch);
						else sha1.put(ch);
					}
				}
			}
			return true;
		}

		// End segment, write sha1string if present
		public void endSegment(char[] sha1string = null)
		{
			if (state == SEG1)
				postProcess();
			assert(state == SEG2);
			enc.compress(-1);
			if (verify && pz.hend)
			{
				pz.run(-1);
				pz.flush();
			}
			enc.@out.put(0);
			enc.@out.put(0);
			enc.@out.put(0);
			enc.@out.put(0);
			if (sha1string)
			{
				enc.@out.put(253);
				for (int i = 0; i < 20; ++i)
					enc.@out.put(sha1string[i]);
			}
			else
				enc.@out.put(254);
			state = BLOCK2;
		}

		// End segment, write checksum and size is verify is true
		public char[] endSegmentChecksum(long[] size = null, bool dosha1 = true)
		{
			if (state == SEG1)
				postProcess();
			assert(state == SEG2);
			enc.compress(-1);
			if (verify && pz.hend)
			{
				pz.run(-1);
				pz.flush();
			}
			enc.@out.put(0);
			enc.@out.put(0);
			enc.@out.put(0);
			enc.@out.put(0);
			if (verify)
			{
				if (size) *size = sha1.usize();
				memcpy(sha1result, sha1.result(), 20);
			}
			if (verify && dosha1)
			{
				enc.@out.put(253);
				for (int i = 0; i < 20; ++i)
					enc.@out.put(sha1result[i]);
			}
			else
				enc.@out.put(254);
			state = BLOCK2;
			return verify ? sha1result : 0;
		}

		public long getSize()
		{
			return (long)sha1.usize();
		}

		public string getChecksum()
		{
			return sha1.result();
		}

		// End block
		public void endBlock()
		{
			assert(state == BLOCK2);
			enc.@out.put(255);
			state = INIT;
		}

		public int stat(int x)
		{
			return enc.stat(x);
		}

		private ZPAQL z, pz; // model and test postprocessor
		private Encoder enc; //arithmetic encoder containing predictor
		private Reader @in; // input source
		private SHA1 sha1; // to test pz output
		private char[] sha1result = new char[20]; // sha1 output
		private State state;
		private bool verify; // if true then test by postprocessing

		private enum State
		{
			INIT,
			BLOCK1,
			SEG1,
			BLOCK2,
			SEG2
		}

		// Memory reader
		private class MemoryReader : Reader
		{
			private char[] p;
			private int pptr;

			public MemoryReader(char[] p_, int pptr)
			{
				p = p_;
				this.pptr = pptr;
			}

			public int Get()
			{
				return p[pptr] & 255;
			}
		}
	}
}
