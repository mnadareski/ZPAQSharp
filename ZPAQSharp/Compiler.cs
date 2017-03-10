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
		// Compile a configuration file. Store COMP/HCOMP section in hcomp.
		// If there is a PCOMP section, store it in pcomp and store the PCOMP
		// command in pcomp_cmd. Replace "$1..$9+n" with args[0..8]+n
		public Compiler(string @in, int[] args, ZPAQL hz, ZPAQL pz, Writer out2)
		{
			this.@in = @in;
			this.args = args;
			this.hz = hz;
			this.pz = pz;
			this.out2 = out2;
			if_stack = new Stack(1000);
			do_stack = new Stack(1000);

			line = 1;
			state = 0;
			hz.clear();
			pz.clear();
			hz.header.resize(68000);

			// Compile the COMP section of header
			rtoken("comp");
			hz.header[2] = rtoken(0, 255);  // hh
			hz.header[3] = rtoken(0, 255);  // hm
			hz.header[4] = rtoken(0, 255);  // ph
			hz.header[5] = rtoken(0, 255);  // pm
			const int n = hz.header[6] = rtoken(0, 255);  // n
			hz.cend = 7;
			for (int i = 0; i < n; ++i)
			{
				rtoken(i, i);
				CompType type = CompType(rtoken(compname));
				hz.header[hz.cend++] = type;
				int clen = libzpaq::compsize[type & 255];
				if (clen < 1 || clen > 10) syntaxError("invalid component");
				for (int j = 1; j < clen; ++j)
					hz.header[hz.cend++] = rtoken(0, 255);  // component arguments
			}
			hz.header[hz.cend++];  // end
			hz.hbegin = hz.hend = hz.cend + 128;

			// Compile HCOMP
			rtoken("hcomp");
			int op = compile_comp(hz);

			// Compute header size
			int hsize = hz.cend - 2 + hz.hend - hz.hbegin;
			hz.header[0] = hsize & 255;
			hz.header[1] = hsize >> 8;

			// Compile POST 0 END
			if (op == POST)
			{
				rtoken(0, 0);
				rtoken("end");
			}

			// Compile PCOMP pcomp_cmd ; program... END
			else if (op == PCOMP)
			{
				pz.header.resize(68000);
				pz.header[4] = hz.header[4];  // ph
				pz.header[5] = hz.header[5];  // pm
				pz.cend = 8;
				pz.hbegin = pz.hend = pz.cend + 128;

				// get pcomp_cmd ending with ";" (case sensitive)
				next();
				while (*in && *in!= ';') {
					if (out2)
						out2->put(*in);
					++in;
				}
				if (*in) ++in;

				// Compile PCOMP
				op = compile_comp(pz);
				int len = pz.cend - 2 + pz.hend - pz.hbegin;  // insert header size
				assert(len >= 0);
				pz.header[0] = len & 255;
				pz.header[1] = len >> 8;
				if (op != END)
					syntaxError("expected END");
			}
			else if (op != END)
				syntaxError("expected END or POST 0 END or PCOMP cmd ; ... END");
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

		// Print error message and exit
		private void syntaxError(string msg, string expected = null) // error()
		{
			Array<char> sbuf(128);  // error message to report
			char* s = &sbuf[0];
			strcat(s, "Config line ");
			for (int i = strlen(s), r = 1000000; r; r /= 10)  // append line number
				if (line / r) s[i++] = '0' + line / r % 10;
			strcat(s, " at ");
			for (int i = strlen(s); i < 40 && *in> ' '; ++i)  // append token found
    s[i] = *in++;
			strcat(s, ": ");
			strncat(s, msg, 40);  // append message
			if (expected)
			{
				strcat(s, ", expected: ");
				strncat(s, expected, 20);  // append expected token if any
			}
			error(s);
		}

		// Advance in to start of next token. Tokens are delimited by white
		// space. Comments inclosed in ((nested) parenthsis) are skipped.
		private void next() // advance in to next token
		{
			assert(in);
			for (; *in; ++in) {
				if (*in== '\n') ++line;
				if (*in== '(') state += 1 + (state < 0);
    else if (state > 0 && *in== ')') --state;
    else if (state < 0 && *in<= ' ') state = 0;
    else if (state == 0 && *in> ' ') { state = -1; break; }
			}
			if (!*in) error("unexpected end of config");
		}

		// return true if in==word up to white space or '(', case insensitive
		private bool matchToken(string tok) // in==token?
		{
			const char* a =in;
			for (; (*a > ' ' && *a != '(' && *word); ++a, ++word)
				if (tolower(*a) != tolower(*word)) return false;
			return !*word && (*a <= ' ' || *a == '(');
		}

		// Read a number in (low...high) or exit with an error
		// For numbers like $N+M, return arg[N-1]+M
		private int rtoken(int low, int high) // return token which must be in range
		{
			next();
			int r = 0;
			if (@in[0]=='$' && @in[1]>='1' && @in[1]<='9')
			{
				if (@in[2]=='+') r=atoi(@in+3);
				if (args) r+=args[@in[1]-'1'];
			}
			else if (@in[0]=='-' || (@in[0]>='0' && @in[0]<='9')) r=atoi(@in);
			else syntaxError("expected a number");
			if (r<low) syntaxError("number too low");
			if (r>high) syntaxError("number too high");
			return r;
		}

		// Read a token which must be the specified value s
		private void rtoken(string s) // return toke which must be s
		{
			assert(s);
			next();
			if (!matchToken(s)) syntaxError("expected", s);
		}

		// Read a token, which must be in the NULL terminated list or else
		// exit with an error. If found, return its index.
		private void rtoken(string[] list)
		{
			assert(in);
			assert(list);
			next();
			for (int i = 0; list[i]; ++i)
				if (matchToken(list[i]))
					return i;
			syntaxError("unexpected");
			assert(0);
			return -1; // not reached
		}

		// Compile HCOMP or PCOMP code. Exit on error. Return
		// code for end token (POST, PCOMP, END)
		private int compile_comp(ZPAQL z) // compile either HCOMP or PCOMP
		{
			int op = 0;
			const int comp_begin = z.hend;
			while (true)
			{
				op = rtoken(opcodelist);
				if (op == POST || op == PCOMP || op == END) break;
				int operand = -1; // 0...255 if 2 bytes
				int operand2 = -1;  // 0...255 if 3 bytes
				if (op == IF)
				{
					op = JF;
					operand = 0; // set later
					if_stack.push(z.hend + 1); // save jump target location
				}
				else if (op == IFNOT)
				{
					op = JT;
					operand = 0;
					if_stack.push(z.hend + 1); // save jump target location
				}
				else if (op == IFL || op == IFNOTL)
				{  // long if
					if (op == IFL) z.header[z.hend++] = (JT);
					if (op == IFNOTL) z.header[z.hend++] = (JF);
					z.header[z.hend++] = (3);
					op = LJ;
					operand = operand2 = 0;
					if_stack.push(z.hend + 1);
				}
				else if (op == ELSE || op == ELSEL)
				{
					if (op == ELSE) op = JMP, operand = 0;
					if (op == ELSEL) op = LJ, operand = operand2 = 0;
					int a = if_stack.pop();  // conditional jump target location
					assert(a > comp_begin && a < int(z.hend));
					if (z.header[a - 1] != LJ)
					{  // IF, IFNOT
						assert(z.header[a - 1] == JT || z.header[a - 1] == JF || z.header[a - 1] == JMP);
						int j = z.hend - a + 1 + (op == LJ); // offset at IF
						assert(j >= 0);
						if (j > 127) syntaxError("IF too big, try IFL, IFNOTL");
						z.header[a] = j;
					}
					else
					{  // IFL, IFNOTL
						int j = z.hend - comp_begin + 2 + (op == LJ);
						assert(j >= 0);
						z.header[a] = j & 255;
						z.header[a + 1] = (j >> 8) & 255;
					}
					if_stack.push(z.hend + 1);  // save JMP target location
				}
				else if (op == ENDIF)
				{
					int a = if_stack.pop();  // jump target address
					assert(a > comp_begin && a < int(z.hend));
					int j = z.hend - a - 1;  // jump offset
					assert(j >= 0);
					if (z.header[a - 1] != LJ)
					{
						assert(z.header[a - 1] == JT || z.header[a - 1] == JF || z.header[a - 1] == JMP);
						if (j > 127) syntaxError("IF too big, try IFL, IFNOTL, ELSEL\n");
						z.header[a] = j;
					}
					else
					{
						assert(a + 1 < int(z.hend));
						j = z.hend - comp_begin;
						z.header[a] = j & 255;
						z.header[a + 1] = (j >> 8) & 255;
					}
				}
				else if (op == DO)
				{
					do_stack.push(z.hend);
				}
				else if (op == WHILE || op == UNTIL || op == FOREVER)
				{
					int a = do_stack.pop();
					assert(a >= comp_begin && a < int(z.hend));
					int j = a - z.hend - 2;
					assert(j <= -2);
					if (j >= -127)
					{  // backward short jump
						if (op == WHILE) op = JT;
						if (op == UNTIL) op = JF;
						if (op == FOREVER) op = JMP;
						operand = j & 255;
					}
					else
					{  // backward long jump
						j = a - comp_begin;
						assert(j >= 0 && j < int(z.hend) - comp_begin);
						if (op == WHILE)
						{
							z.header[z.hend++] = (JF);
							z.header[z.hend++] = (3);
						}
						if (op == UNTIL)
						{
							z.header[z.hend++] = (JT);
							z.header[z.hend++] = (3);
						}
						op = LJ;
						operand = j & 255;
						operand2 = j >> 8;
					}
				}
				else if ((op & 7) == 7)
				{ // 2 byte operand, read N
					if (op == LJ)
					{
						operand = rtoken(0, 65535);
						operand2 = operand >> 8;
						operand &= 255;
					}
					else if (op == JT || op == JF || op == JMP)
					{
						operand = rtoken(-128, 127);
						operand &= 255;
					}
					else
						operand = rtoken(0, 255);
				}
				if (op >= 0 && op <= 255)
					z.header[z.hend++] = (op);
				if (operand >= 0)
					z.header[z.hend++] = (operand);
				if (operand2 >= 0)
					z.header[z.hend++] = (operand2);
				if (z.hend >= z.header.isize() - 130 || z.hend - z.hbegin + z.cend - 2 > 65535)
					syntaxError("program too big");
			}
			z.header[z.hend++] = (0); // END
			return op;
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

		// Component names
		string[] compname = new string[256]
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
		string[] opcodelist = new string[272] {
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
"while","until","forever","ifl","ifnotl","elsel",";",    0};

		// convert to lower case
		int tolower(int c) { return (c >= 'A' && c <= 'Z') ? c + 'a' - 'A' : c; }
	}
}
