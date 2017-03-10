using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZPAQSharp
{
	class PostProcessor
	{
		int state;   // input parse state: 0=INIT, 1=PASS, 2..4=loading, 5=POST
		int hsize;   // header size
		int ph, pm;  // sizes of H and M in z

		public ZPAQL z; // holds PCOMP
		
		public PostProcessor()
		{
			state = 0;
			hsize = 0;
			ph = 0;
			pm = 0;
		}

		// Copy ph, pm from block header
		public void init(int h, int m) // ph, pm sizes of H and M
		{
			state = hsize = 0;
			ph = h;
			pm = m;
			z.clear();
		}

		// (PASS=0 | PROG=1 psize[0..1] pcomp[0..psize-1]) data... EOB=-1
		// Return state: 1=PASS, 2..4=loading PROG, 5=PROG loaded
		public int write(int c) // Input a byte, return state
		{
			assert(c >= -1 && c <= 255);
			switch (state)
			{
				case 0:  // initial state
					if (c < 0) error("Unexpected EOS");
					state = c + 1;  // 1=PASS, 2=PROG
					if (state > 2) error("unknown post processing type");
					if (state == 1) z.clear();
					break;
				case 1:  // PASS
					z.@outc(c);
					break;
				case 2: // PROG
					if (c < 0) error("Unexpected EOS");
					hsize = c;  // low byte of size
					state = 3;
					break;
				case 3:  // PROG psize[0]
					if (c < 0) error("Unexpected EOS");
					hsize += c * 256;  // high byte of psize
					if (hsize < 1) error("Empty PCOMP");
					Array.Resize(ref z.header, hsize + 300);
					z.cend = 8;
					z.hbegin = z.hend = z.cend + 128;
					z.header[4] = ph;
					z.header[5] = pm;
					state = 4;
					break;
				case 4:  // PROG psize[0..1] pcomp[0...]
					if (c < 0) error("Unexpected EOS");
					assert(z.hend < z.header.Length);
					z.header[z.hend++] = c;  // one byte of pcomp
					if (z.hend - z.hbegin == hsize)
					{  // last byte of pcomp?
						hsize = z.cend - 2 + z.hend - z.hbegin;
						z.header[0] = hsize & 255;  // header size with empty COMP
						z.header[1] = hsize >> 8;
						z.@initp();
						state = 5;
					}
					break;
				case 5:  // PROG ... data
					z.run(c);
					if (c < 0) z.flush();
					break;
			}
			return state;
		}

		public int getState()
		{
			return state;
		}

		public void setOutput(Writer @out)
		{
			z.@output = @out;
		}

		public void setSHA1(SHA1 sha1ptr)
		{
			z.sha1 = sha1ptr;
		}
	}
}
