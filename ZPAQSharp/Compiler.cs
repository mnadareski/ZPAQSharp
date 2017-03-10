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
	// Input ZPAQL source code with args and store the compiled code
	// in hz and pz and write pcomp_cmd to out2.
	class Compiler
	{
		public Compiler(string @in, int[] args, ZPAQL hz, Writer out2)
		{
		}

		private string @in; // ZPAQL source code
		private int[] args; // Array of up to 9 args, default NULL = all 0
		private ZPAQL hz; // Output of COMP and HCOMP sections
		private ZPAQL pz; // Output of PCOMP section
		private Writer out2; // Output  ... of "PCOMP ... ;"
		private int line; // Input line number for reporting errors
		private int state; // parse state: 0 = space, -1 = word >0 (nest level)

		// Symbolic constants
		private enum CompType
		{
			NONE, CONS, CM, ICM, MATCH, AVG, MIX2, MIX, ISSE, SSE,
			JT = 39, JF = 47, JMP = 63, LJ = 255,
			POST = 256, PCOMP, END, IF, IFNOT, ELSE, ENDIF, DO,
			WHILE, UNTIL, FOREVER, IFL, IFNOTL, ELSEL, SEMICOLON
		}

		private void syntaxError(string msg, string expected = null) // error()
		{
		}

		private void next() // advance in to next token
		{
		}

		private bool matchToken(string tok) // in==token?
		{
			return false;
		}

		private int rtoken(int low, int high) // return token which must be in range
		{
			return 0;
		}

		private void rtoken(string s) // return toke which must be s
		{
		}

		private int compile_comp(ZPAQL z) // compile either HCOMP or PCOMP
		{
			return 0;
		}

		// Stack of n elements
		private class Stack
		{
			Array<U16> s;
			ulong top;

			public Stack(int n)
			{
				s = new Array<U16>((ulong)n);
				top = 0;
			}

			public void push(U16 x)
			{
				if (top >= s.size())
				{
					LibZPAQ.error("IF or DO nested too deep");
				}

				s[top++] = x;
			}

			public U16 pop()
			{
				if (top <= 0)
				{
					LibZPAQ.error("unmatched IF or DO");
				}

				return s[--top];
			}
		}

		Stack if_stack, do_stack;
	}
}
