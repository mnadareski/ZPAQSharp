using System;
using System.Text;

namespace ZPAQSharp
{
	// Input ZPAQL source code with args and store the compiled code
	// in hz and pz and write pcomp_cmd to out2.
	class Compiler
	{
		// Compile a configuration file. Store COMP/HCOMP section in hcomp.
		// If there is a PCOMP section, store it in pcomp and store the PCOMP
		// command in pcomp_cmd. Replace "$1..$9+n" with args[0..8]+n
		public Compiler(char[] @in, int[] args, ZPAQL hz, ZPAQL pz, Writer out2)
		{
			this.@in = @in;
			this.args = args;
			this.hz = hz;
			this.pz = pz;
			this.@out2 = out2;
			if_stack = new Stack(1000);
			do_stack = new Stack(1000);

			line = 1;
			state = 0;
			hz.clear();
			pz.clear();
			Array.Resize(ref hz.header, 68000);

			// Compile the COMP section of header
			RToken("comp");
			hz.header[2] = (byte)RToken(0, 255);  // hh
			hz.header[3] = (byte)RToken(0, 255);  // hm
			hz.header[4] = (byte)RToken(0, 255);  // ph
			hz.header[5] = (byte)RToken(0, 255);  // pm
			int n = hz.header[6] = (byte)RToken(0, 255);  // n
			hz.cend = 7;
			for (int i = 0; i < n; ++i)
			{
				RToken(i, i);
				CompType type = (CompType)RToken(compname);
				hz.header[(ulong)hz.cend++] = (byte)type;
				int clen = LibZPAQ.compsize[(int)type & 255];
				if (clen < 1 || clen > 10) SyntaxError("invalid component");
				for (int j = 1; j < clen; ++j)
				{
					hz.header[(ulong)hz.cend++] = (byte)RToken(0, 255);  // component arguments
				}
			}
			hz.cend++;  // end
			hz.hbegin = hz.hend = hz.cend + 128;

			// Compile HCOMP
			RToken("hcomp");
			int op = CompileComp(hz);

			// Compute header size
			int hsize = hz.cend - 2 + hz.hend - hz.hbegin;
			hz.header[0] = (byte)(hsize & 255);
			hz.header[1] = (byte)(hsize >> 8);

			// Compile POST 0 END
			if (op == (int)CompType.POST)
			{
				RToken(0, 0);
				RToken("end");
			}

			// Compile PCOMP pcomp_cmd ; program... END
			else if (op == (int)CompType.PCOMP)
			{
				Array.Resize(ref pz.header, 68000);
				pz.header[4] = hz.header[4];  // ph
				pz.header[5] = hz.header[5];  // pm
				pz.cend = 8;
				pz.hbegin = pz.hend = pz.cend + 128;

				// get pcomp_cmd ending with ";" (case sensitive)
				Next();
				while (@in[inptr] != '\0' && @in[inptr] != ';')
				{
					if (out2 != null)
					{
						out2.put(@in[inptr]);
					}
					++inptr;
				}
				if (@in != null)
				{
					++inptr;
				}

				// Compile PCOMP
				op = CompileComp(pz);
				int len = pz.cend - 2 + pz.hend - pz.hbegin;  // insert header size

				if (len < 0)
				{
					throw new Exception("Header size must be greater than 0");
				}
				pz.header[0] = (byte)(len & 255);
				pz.header[1] = (byte)(len >> 8);
				if (op != (int)CompType.END)
				{
					SyntaxError("expected END");
				}
			}
			else if (op != (int)CompType.END)
			{
				SyntaxError("expected END or POST 0 END or PCOMP cmd ; ... END");
			}
		}

		private char[] @in; // ZPAQL source code
		private int inptr; // Pointer to the array
		private int[] args; // Array of up to 9 args, default NULL = all 0
		private ZPAQL hz; // Output of COMP and HCOMP sections
		private ZPAQL pz; // Output of PCOMP section
		private Writer out2; // Output  ... of "PCOMP ... ;"
		private int line; // Input line number for reporting errors
		private int state; // parse state: 0 = space, -1 = word >0 (nest level)

		// Symbolic constants
		private enum CompType
		{
			NONE,
			CONS,
			CM,
			ICM,
			MATCH,
			AVG,
			MIX2,
			MIX,
			ISSE,
			SSE,
			JT = 39,
			JF = 47,
			JMP = 63,
			LJ = 255,
			POST = 256,
			PCOMP,
			END,
			IF,
			IFNOT,
			ELSE,
			ENDIF,
			DO,
			WHILE,
			UNTIL,
			FOREVER,
			IFL,
			IFNOTL,
			ELSEL,
			SEMICOLON
		}

		// Print error message and exit
		// TODO: Work on commented code
		private void SyntaxError(string msg, string expected = null) // error()
		{
			StringBuilder sbuf = new StringBuilder();
			sbuf.Append("Config line ");

			/*
			for (int i = sbuf.Length, r = 1000000; r > 0; r /= 10) // append line number
			{
				if (line / r > 0)
				{
					sbuf.Append("0", line / r % 10);
				}
			}
			*/
			sbuf.Append(" at ");
			/*
			for (int i = sbuf.Length; i < 40 && @in[inptr] > ' '; ++i) // append token found
			{
				sbuf.Append(@in[inptr++]);
			}
			*/
			sbuf.Append(": ");
			sbuf.Append(msg.Substring(0, 40));
			if (expected != null)
			{
				sbuf.Append(", expected: ");
				sbuf.Append(expected.Substring(0, 20)); // append expected token if any
			}
			LibZPAQ.error(sbuf.ToString());
		}

		// Advance in to start of next token. Tokens are delimited by white
		// space. Comments inclosed in ((nested) parenthsis) are skipped.
		private void Next() // advance in to next token
		{
			if (@in == null)
			{
				throw new Exception("Expected ZPAQL source code to be non-null");
			}

			for (; @in[inptr] != '\0'; ++inptr)
			{
				if (@in[inptr] == '\n')
				{
					++line;
				}
				if (@in[inptr] == '(')
				{
					state += 1 + (state < 0 ? 1 : 0);
				}
				else if (state > 0 && @in[inptr] == ')')
				{
					--state;
				}
				else if (state < 0 && @in[inptr] <= ' ')
				{
					state = 0;
				}
				else if (state == 0 && @in[inptr] > ' ')
				{
					state = -1;
					break;
				}
			}
			if (inptr != @in.Length) // !*in
			{
				LibZPAQ.error("unexpected end of config");
			}
		}

		// return true if in==word up to white space or '(', case insensitive
		private bool MatchToken(char[] word, int wordptr) // in==token?
		{
			int a = inptr;
			for (; (@in[a] > ' ' && @in[a] != '(' && word[wordptr] != '\0'); ++a, ++wordptr)
			{
				if (ToLower(@in[a]) != ToLower(word[wordptr]))
				{
					return false;
				}
			}
			return word[wordptr] != '\0' && (@in[a] <= ' ' || @in[a] == '(');
		}

		// Read a number in (low...high) or exit with an error
		// For numbers like $N+M, return arg[N-1]+M
		private int RToken(int low, int high) // return token which must be in range
		{
			Next();
			int r = 0;
			if (@in[0] == '$' && @in[1] >= '1' && @in[1] <= '9')
			{
				if (@in[2] == '+')
				{
					r = (int)@in[inptr] + 3;
				}
				if (args != null)
				{
					r += args[@in[1] - '1'];
				}
			}
			else if (@in[0] == '-' || (@in[0] >= '0' && @in[0] <= '9'))
			{
				r = (int)@in[inptr];
			}
			else
			{
				SyntaxError("expected a number");
			}
			if (r < low)
			{
				SyntaxError("number too low");
			}
			if (r > high)
			{
				SyntaxError("number too high");
			}
			return r;
		}

		// Read a token which must be the specified value s
		private void RToken(string s) // return token which must be s
		{
			if (s != null)
			{
				throw new ArgumentNullException(nameof(s));
			}
			Next();
			if (!MatchToken(s.ToCharArray(), 0))
			{
				SyntaxError("expected", s.ToString());
			}
		}

		// Read a token, which must be in the NULL terminated list or else
		// exit with an error. If found, return its index.
		private int RToken(string[] list)
		{
			if (@in == null)
			{
				throw new Exception("Expected ZPAQL source code to be non-null");
			}
			if (list == null)
			{
				throw new ArgumentNullException(nameof(list));
			}
			Next();
			for (int i = 0; list[i] != null; ++i)
			{
				if (MatchToken(list[i].ToCharArray(), 0))
				{
					return i;
				}
			}

			SyntaxError("unexpected");
			throw new Exception("Unexpected end of source code");
		}

		// Compile HCOMP or PCOMP code. Exit on error. Return
		// code for end token (POST, PCOMP, END)
		private int CompileComp(ZPAQL z) // compile either HCOMP or PCOMP
		{
			int op = 0;
			int comp_begin = z.hend;
			while (true)
			{
				op = RToken(opcodelist);
				if (op == (int)CompType.POST || op == (int)CompType.PCOMP || op == (int)CompType.END)
				{
					break;
				}

				int operand = -1; // 0...255 if 2 bytes
				int operand2 = -1;  // 0...255 if 3 bytes
				if (op == (int)CompType.IF)
				{
					op = (int)CompType.JF;
					operand = 0; // set later
					if_stack.Push((ushort)(z.hend + 1)); // save jump target location
				}
				else if (op == (int)CompType.IFNOT)
				{
					op = (int)CompType.JT;
					operand = 0;
					if_stack.Push((ushort)(z.hend + 1)); // save jump target location
				}
				else if (op == (int)CompType.IFL || op == (int)CompType.IFNOTL) // long if
				{
					if (op == (int)CompType.IFL)
					{
						z.header[z.hend++] = (int)CompType.JT;
					}
					if (op == (int)CompType.IFNOTL)
					{
						z.header[z.hend++] = (int)CompType.JF;
					}
					z.header[z.hend++] = (3);
					op = (int)CompType.LJ;
					operand = operand2 = 0;
					if_stack.Push((ushort)(z.hend + 1));
				}
				else if (op == (int)CompType.ELSE || op == (int)CompType.ELSEL)
				{
					if (op == (int)CompType.ELSE)
					{
						op = (int)CompType.JMP;
						operand = 0;
					}
					if (op == (int)CompType.ELSEL)
					{
						op = (int)CompType.LJ;
						operand = operand2 = 0;
					}
					int a = if_stack.Pop();  // conditional jump target location
					if (a <= comp_begin || a >= z.hend)
					{
						throw new Exception("Expected target location to be within the bounds");
					}
					if (z.header[a - 1] != (int)CompType.LJ) // IF, IFNOT
					{
						// TODO: Ended here
						assert(z.header[a - 1] == (int)CompType.JT || z.header[a - 1] == (int)CompType.JF || z.header[a - 1] == (int)CompType.JMP);
						int j = z.hend - a + 1 + (op == (int)CompType.LJ ? 1 : 0); // offset at IF
						assert(j >= 0);
						if (j > 127) SyntaxError("IF too big, try IFL, IFNOTL");
						z.header[a] = (byte)j;
					}
					else
					{  // IFL, IFNOTL
						int j = z.hend - comp_begin + 2 + (op == (int)CompType.LJ ? 1 : 0);
						assert(j >= 0);
						z.header[a] = j & 255;
						z.header[a + 1] = (j >> 8) & 255;
					}
					if_stack.Push((ushort)(z.hend + 1));  // save JMP target location
				}
				else if (op == (int)CompType.ENDIF)
				{
					int a = if_stack.Pop();  // jump target address
					assert(a > comp_begin && a < (int)(z.hend));
					int j = z.hend - a - 1;  // jump offset
					assert(j >= 0);
					if (z.header[a - 1] != (int)CompType.LJ)
					{
						assert(z.header[a - 1] == (int)CompType.JT || z.header[a - 1] == (int)CompType.JF || z.header[a - 1] == (int)CompType.JMP);
						if (j > 127) SyntaxError("IF too big, try IFL, IFNOTL, ELSEL\n");
						z.header[a] = j;
					}
					else
					{
						assert(a + 1 < (int)(z.hend));
						j = z.hend - comp_begin;
						z.header[a] = j & 255;
						z.header[a + 1] = (j >> 8) & 255;
					}
				}
				else if (op == (int)CompType.DO)
				{
					do_stack.Push((ushort)z.hend);
				}
				else if (op == (int)CompType.WHILE || op == (int)CompType.UNTIL || op == (int)CompType.FOREVER)
				{
					int a = do_stack.Pop();
					assert(a >= comp_begin && a < (int)(z.hend));
					int j = a - z.hend - 2;
					assert(j <= -2);
					if (j >= -127)
					{  // backward short jump
						if (op == (int)CompType.WHILE) op = (int)CompType.JT;
						if (op == (int)CompType.UNTIL) op = (int)CompType.JF;
						if (op == (int)CompType.FOREVER) op = (int)CompType.JMP;
						operand = j & 255;
					}
					else
					{  // backward long jump
						j = a - comp_begin;
						assert(j >= 0 && j < (int)(z.hend) - comp_begin);
						if (op == (int)CompType.WHILE)
						{
							z.header[z.hend++] = ((int)CompType.JF);
							z.header[z.hend++] = (3);
						}
						if (op == (int)CompType.UNTIL)
						{
							z.header[z.hend++] = ((int)CompType.JT);
							z.header[z.hend++] = (3);
						}
						op = (int)CompType.LJ;
						operand = j & 255;
						operand2 = j >> 8;
					}
				}
				else if ((op & 7) == 7)
				{ // 2 byte operand, read N
					if (op == (int)CompType.LJ)
					{
						operand = RToken(0, 65535);
						operand2 = operand >> 8;
						operand &= 255;
					}
					else if (op == (int)CompType.JT || op == (int)CompType.JF || op == (int)CompType.JMP)
					{
						operand = RToken(-128, 127);
						operand &= 255;
					}
					else
						operand = RToken(0, 255);
				}
				if (op >= 0 && op <= 255)
					z.header[z.hend++] = (op);
				if (operand >= 0)
					z.header[z.hend++] = (operand);
				if (operand2 >= 0)
					z.header[z.hend++] = (operand2);
				if (z.hend >= z.header.Length - 130 || z.hend - z.hbegin + z.cend - 2 > 65535)
					SyntaxError("program too big");
			}
			z.header[z.hend++] = (0); // END
			return op;
		}

		// Stack of n elements
		private class Stack
		{
			ushort[] s;
			int top;

			public Stack(int n)
			{
				s = new ushort[n];
				top = 0;
			}

			public void Push(ushort x)
			{
				if (top >= s.Length)
				{
					LibZPAQ.error("IF or DO nested too deep");
				}

				s[top++] = x;
			}

			public ushort Pop()
			{
				if (top <= 0)
				{
					LibZPAQ.error("unmatched IF or DO");
				}

				return s[--top];
			}
		}

		Stack if_stack, do_stack;

		// Component names
		private string[] compname = new string[256]
			{"","const","cm","icm","match","avg","mix2","mix","isse","sse",null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,
			null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null};

		// Opcodes
		private string[] opcodelist = new string[272] {
		"error","a++",  "a--",  "a!",   "a=0",  "",     "",     "a=r",
		"b<>a", "b++",  "b--",  "b!",   "b=0",  "",     "",     "b=r",
		"c<>a", "c++",  "c--",  "c!",   "c=0",  "",     "",     "c=r",
		"d<>a", "d++",  "d--",  "d!",   "d=0",  "",     "",     "d=r",
		"*b<>a","*b++", "*b--", "*b!",  "*b=0", "",     "",     "jt",
		"*c<>a","*c++", "*c--", "*c!",  "*c=0", "",     "",     "jf",
		"*d<>a","*d++", "*d--", "*d!",  "*d=0", "",     "",     "r=a",
		"halt", "out",  "",     "hash", "hashd","",     "",     "jmp",
		"a=a",  "a=b",  "a=c",  "a=d",  "a=*b", "a=*c", "a=*d", "a=",
		"b=a",  "b=b",  "b=c",  "b=d",  "b=*b", "b=*c", "b=*d", "b=",
		"c=a",  "c=b",  "c=c",  "c=d",  "c=*b", "c=*c", "c=*d", "c=",
		"d=a",  "d=b",  "d=c",  "d=d",  "d=*b", "d=*c", "d=*d", "d=",
		"*b=a", "*b=b", "*b=c", "*b=d", "*b=*b","*b=*c","*b=*d","*b=",
		"*c=a", "*c=b", "*c=c", "*c=d", "*c=*b","*c=*c","*c=*d","*c=",
		"*d=a", "*d=b", "*d=c", "*d=d", "*d=*b","*d=*c","*d=*d","*d=",
		"",     "",     "",     "",     "",     "",     "",     "",
		"a+=a", "a+=b", "a+=c", "a+=d", "a+=*b","a+=*c","a+=*d","a+=",
		"a-=a", "a-=b", "a-=c", "a-=d", "a-=*b","a-=*c","a-=*d","a-=",
		"a*=a", "a*=b", "a*=c", "a*=d", "a*=*b","a*=*c","a*=*d","a*=",
		"a/=a", "a/=b", "a/=c", "a/=d", "a/=*b","a/=*c","a/=*d","a/=",
		"a%=a", "a%=b", "a%=c", "a%=d", "a%=*b","a%=*c","a%=*d","a%=",
		"a&=a", "a&=b", "a&=c", "a&=d", "a&=*b","a&=*c","a&=*d","a&=",
		"a&~a", "a&~b", "a&~c", "a&~d", "a&~*b","a&~*c","a&~*d","a&~",
		"a|=a", "a|=b", "a|=c", "a|=d", "a|=*b","a|=*c","a|=*d","a|=",
		"a^=a", "a^=b", "a^=c", "a^=d", "a^=*b","a^=*c","a^=*d","a^=",
		"a<<=a","a<<=b","a<<=c","a<<=d","a<<=*b","a<<=*c","a<<=*d","a<<=",
		"a>>=a","a>>=b","a>>=c","a>>=d","a>>=*b","a>>=*c","a>>=*d","a>>=",
		"a==a", "a==b", "a==c", "a==d", "a==*b","a==*c","a==*d","a==",
		"a<a",  "a<b",  "a<c",  "a<d",  "a<*b", "a<*c", "a<*d", "a<",
		"a>a",  "a>b",  "a>c",  "a>d",  "a>*b", "a>*c", "a>*d", "a>",
		"",     "",     "",     "",     "",     "",     "",     "",
		"",     "",     "",     "",     "",     "",     "",     "lj",
		"post", "pcomp","end",  "if",   "ifnot","else", "endif","do",
		"while","until","forever","ifl","ifnotl","elsel",";",    null};

		// convert to lower case
		private int ToLower(int c) { return (c >= 'A' && c <= 'Z') ? c + 'a' - 'A' : c; }
	}
}
