using System;
using System.Collections.Generic;
using System.Linq;
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

		public void writeTag()
		{
		}

		public void startBlock(int level) // level=1,2,3
		{
		}

		public void startBlock(string hcomp) // ZPAQL byte code
		{
		}

		public void startBlock(string config, // ZPAQL source code
			int[] args, // NULL or int[9] arguments
			Writer pcomp_cmd = null) // retrieve preprocessor command
		{
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

		public void startSegment(string filename = null, string comment = null)
		{
		}

		public void setInput(Reader i)
		{
			@in = i;
		}

		public void postProcess(string pcomp = null, string comment = null) // byte code
		{
		}

		public bool compress(int n = -1) // n bytes, -1=all, return true until done
		{
			return false;
		}

		public void enSegment(string sha1string = null)
		{
		}

		public char[] endSegmentChecksum(long[] size = null, bool dosha1 = true)
		{
			return new char[0];
		}

		public long getSize()
		{
			return (long)sha1.usize();
		}

		public string getChecksum()
		{
			return sha1.result();
		}

		public void endBlock()
		{
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
			INIT, BLOCK1, SEG1, BLOCK2, SEG2
		}
	}
}
