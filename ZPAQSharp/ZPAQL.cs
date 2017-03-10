using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using byte = System.Byte;
using ushort = System.UInt16;
using uint = System.UInt32;
using ulong = System.UInt64;

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
			Array.Resize(ref outbuf, 1 << 14);
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
			Array.Resize(ref header, 0);
			Array.Resize(ref h, 0);
			Array.Resize(ref m, 0);
			Array.Resize(ref r, 0);
			allocx(rcode, rcode_size, 0);
		}

		public void inith() // Initialize as HCOMP to run
		{
			assert(header.Length > 6);
			assert(output == 0);
			assert(sha1 == 0);
			init(header[2], header[3]); // hh, hm
		}

		public void initp() // Initialize as PCOMP to run
		{
			assert(header.Length > 6);
			init(header[4], header[5]); // ph, pm
		}

		public double memory() // Return memory requirement in bytes
		{
			double mem = pow2(header[2] + 2) + pow2(header[3])  // hh hm
			+ pow2(header[4] + 2) + pow2(header[5])  // ph pm
			+ header.Length;
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

		// Execute the ZPAQL code with input byte or -1 for EOF.
		// Use JIT code at rcode if available, or else create it.
		public void run(uint input) // Execute with input
		{
# ifdef NOJIT
			run0(input);
#else
			if (!rcode)
			{
				allocx(rcode, rcode_size, (hend * 10 + 4096) & -4096);
				int n = assemble();
				if (n > rcode_size)
				{
					allocx(rcode, rcode_size, n);
					n = assemble();
				}
				if (!rcode || n < 10 || rcode_size < 10)
					error("run JIT failed");
			}
			a = input;
			const uint rc = ((int(*)())(&rcode[0]))();
			if (rc == 0) return;
			else if (rc == 1) libzpaq::error("Bad ZPAQL opcode");
			else if (rc == 2) libzpaq::error("Out of memory");
			else if (rc == 3) libzpaq::error("Write error");
			else libzpaq::error("ZPAQL execution error");
#endif
		}

		public int read(Reader in2) // Read header
		{
			/ Get header size and allocate
  int hsize = in2.get();
			hsize += in2.get() * 256;
			Array.Resize(ref header, hsize + 300);
			cend = hbegin = hend = 0;
			header[cend++] = hsize & 255;
			header[cend++] = hsize >> 8;
			while (cend < 7) header[cend++] = in2.get(); // hh hm ph pm n

			// Read COMP
			int n = header[cend - 1];
			for (int i = 0; i < n; ++i)
			{
				int type = in2.get();  // component type
				if (type < 0 || type > 255) error("unexpected end of file");
				header[cend++] = type;  // component type
				int size = compsize[type];
				if (size < 1) error("Invalid component type");
				if (cend + size > hsize) error("COMP overflows header");
				for (int j = 1; j < size; ++j)
					header[cend++] = in2.get();
			}
			if ((header[cend++] = in2.get()) != 0) error("missing COMP END");

			// Insert a guard gap and read HCOMP
			hbegin = hend = cend + 128;
			if (hend > hsize + 129) error("missing HCOMP");
			while (hend < hsize + 129)
			{
				assert(hend < header.Length - 8);
				int op = in2.get();
				if (op == -1) error("unexpected end of file");
				header[hend++] = op;
			}
			if ((header[hend++] = in2.get()) != 0) error("missing HCOMP END");
			assert(cend >= 7 && cend < header.Length);
			assert(hbegin == cend + 128 && hbegin < header.Length);
			assert(hend > hbegin && hend < header.Length);
			assert(hsize == header[0] + 256 * header[1]);
			assert(hsize == cend - 2 + hend - hbegin);
			allocx(rcode, rcode_size, 0);  // clear JIT code
			return cend + hend - hbegin;
		}

		public bool write(Writer out2, bool pp) // If pp write PCOMP else HCOMP header
		{
			if (header.Length <= 6) return false;
			assert(header[0] + 256 * header[1] == cend - 2 + hend - hbegin);
			assert(cend >= 7);
			assert(hbegin >= cend);
			assert(hend >= hbegin);
			assert(out2);
			if (!pp)
			{  // if not a postprocessor then write COMP
				for (int i = 0; i < cend; ++i)
					out2.put(header[i]);
			}
			else
			{  // write PCOMP size only
				out2.put((hend - hbegin) & 255);
				out2.put((hend - hbegin) >> 8);
			}
			for (int i = hbegin; i < hend; ++i)
				out2.put(header[i]);
			return true;
		}

		public int step(uint input, int mode) // Trace execution (defined externally)
		{
			return 0;
		}

		public Writer output;   // Destination for OUT instruction, or 0 to suppress
		public SHA1 sha1;       // Points to checksum computer
		
		public uint H(int i)		// get element of h
		{
			return h[(ulong)i];
		}

		public void flush() // write outbuf[0..bufptr-1] to output and sha1
		{
			if (output) output.write(&outbuf[0], bufptr);
			if (sha1) sha1.write(&outbuf[0], bufptr);
			bufptr = 0;
		}

		public void outc(int ch) // output byte ch (0..255) or -1 at EOS
		{
			if (ch < 0 || (outbuf[bufptr] = ch && ++bufptr == outbuf.Length))
			{
				flush();
			}
		}
		// ZPAQ1 block header
		public byte[] header;   // hsize[2] hh hm ph pm n COMP (guard) HCOMP (guard)
		public int cend;           // COMP in header[7...cend-1]
		public int hbegin, hend;   // HCOMP/PCOMP in header[hbegin...hend-1]

		// Machine state for executing HCOMP
		private byte[] m;        // memory array M for HCOMP
		private uint[] h;       // hash array H for HCOMP
		private uint[] r;       // 256 element register array
		private char[] outbuf; // output buffer
		private int bufptr;         // number of bytes in outbuf
		private uint a, b, c, d;     // machine registers
		private int f;              // condition flag
		private int pc;             // program counter
		private int rcode_size;     // length of rcode
		private byte[] rcode;         // JIT code for run()

		// Support code
		/*
assemble();

Assembles the ZPAQL code in hcomp[0..hlen-1] and stores x86-32 or x86-64
code in rcode[0..rcode_size-1]. Execution begins at rcode[0]. It will not
write beyond the end of rcode, but in any case it returns the number of
bytes that would have been written. It returns 0 in case of error.

The assembled code implements int run() and returns 0 if successful,
1 if the ZPAQL code executes an invalid instruction or jumps out of
bounds, or 2 if OUT throws bad_alloc, or 3 for other OUT exceptions.

A ZPAQL virtual machine has the following state. All values are
unsigned and initially 0:

  a, b, c, d: 32 bit registers (pointed to by their respective parameters)
  f: 1 bit flag register (pointed to)
  r[0..255]: 32 bit registers
  m[0..msize-1]: 8 bit registers, where msize is a power of 2
  h[0..hsize-1]: 32 bit registers, where hsize is a power of 2
  out: pointer to a Writer
  sha1: pointer to a SHA1

Generally a ZPAQL machine is used to compute contexts which are
placed in h. A second machine might post-process, and write its
output to out and sha1. In either case, a machine is called with
its input in a, representing a single byte (0..255) or
(for a postprocessor) EOF (0xffffffff). Execution returs after a
ZPAQL halt instruction.

ZPAQL instructions are 1 byte unless the last 3 bits are 1.
In this case, a second operand byte follows. Opcode 255 is
the only 3 byte instruction. They are organized:

  00dddxxx = unary opcode xxx on destination ddd (ddd < 111)
  00111xxx = special instruction xxx
  01dddsss = assignment: ddd = sss (ddd < 111)
  1xxxxsss = operation xxxx from sss to a

The meaning of sss and ddd are as follows:

  000 = a   (accumulator)
  001 = b
  010 = c
  011 = d
  100 = *b  (means m[b mod msize])
  101 = *c  (means m[c mod msize])
  110 = *d  (means h[d mod hsize])
  111 = n   (constant 0..255 in second byte of instruction)

For example, 01001110 assigns *d to b. The other instructions xxx
are as follows:

Group 00dddxxx where ddd < 111 and xxx is:
  000 = ddd<>a, swap with a (except 00000000 is an error, and swap
        with *b or *c leaves the high bits of a unchanged)
  001 = ddd++, increment
  010 = ddd--, decrement
  011 = ddd!, not (invert all bits)
  100 = ddd=0, clear (set all bits of ddd to 0)
  101 = not used (error)
  110 = not used
  111 = ddd=r n, assign from r[n] to ddd, n=0..255 in next opcode byte
Except:
  00100111 = jt n, jump if f is true (n = -128..127, relative to next opcode)
  00101111 = jf n, jump if f is false (n = -128..127)
  00110111 = r=a n, assign r[n] = a (n = 0..255)

Group 00111xxx where xxx is:
  000 = halt (return)
  001 = output a
  010 = not used
  011 = hash: a = (a + *b + 512) * 773
  100 = hashd: *d = (*d + a + 512) * 773
  101 = not used
  110 = not used
  111 = unconditional jump (n = -128 to 127, relative to next opcode)
  
Group 1xxxxsss where xxxx is:
  0000 = a += sss (add, subtract, multiply, divide sss to a)
  0001 = a -= sss
  0010 = a *= sss
  0011 = a /= sss (unsigned, except set a = 0 if sss is 0)
  0100 = a %= sss (remainder, except set a = 0 if sss is 0)
  0101 = a &= sss (bitwise AND)
  0110 = a &= ~sss (bitwise AND with complement of sss)
  0111 = a |= sss (bitwise OR)
  1000 = a ^= sss (bitwise XOR)
  1001 = a <<= (sss % 32) (left shift by low 5 bits of sss)
  1010 = a >>= (sss % 32) (unsigned, zero bits shifted in)
  1011 = a == sss (compare, set f = true if equal or false otherwise)
  1100 = a < sss (unsigned compare, result in f)
  1101 = a > sss (unsigned compare)
  1110 = not used
  1111 = not used except 11111111 is a 3 byte jump to the absolute address
         in the next 2 bytes in little-endian (LSB first) order.

assemble() translates ZPAQL to 32 bit x86 code to be executed by run().
Registers are mapped as follows:

  eax = source sss from *b, *c, *d or sometimes n
  ecx = pointer to destination *b, *c, *d, or spare
  edx = a
  ebx = f (1 for true, 0 for false)
  esp = stack pointer
  ebp = d
  esi = b
  edi = c

run() saves non-volatile registers (ebp, esi, edi, ebx) on the stack,
loads a, b, c, d, f, and executes the translated instructions.
A halt instruction saves a, b, c, d, f, pops the saved registers
and returns. Invalid instructions or jumps outside of the range
of the ZPAQL code call libzpaq::error().

In 64 bit mode, the following additional registers are used:

  r12 = h
  r14 = r
  r15 = m

*/
		// Assemble ZPAQL in in the HCOMP section of header to rcode,
		// but do not write beyond rcode_size. Return the number of
		// bytes output or that would have been output.
		// Execution starts at rcode[0] and returns 1 if successful or 0
		// in case of a ZPAQL execution error.
		private int assemble() // put JIT code in rcode
		{
			// x86? (not foolproof)
			const int S = sizeof(char);      // 4 = x86, 8 = x86-64
			uint t = 0x12345678;
			if (*(char*)&t != 0x78 || (S != 4 && S != 8))
				error("JIT supported only for x86-32 and x86-64");

			byte hcomp = header[hbegin];
			int hlen = hend - hbegin + 2;
			int msize = m.Length;
			int hsize = h.Length;
			int[] regcode = new int[8] { 2, 6, 7, 5, 0, 0, 0, 0 }; // a,b,c,d.. . edx,esi,edi,ebp,eax..
			int[] it = new int[hlen];            // hcomp . rcode locations
			int done = 0;  // number of instructions assembled (0..hlen)
			int o = 5;  // rcode output index, reserve space for jmp

			// Code for the halt instruction (restore registers and return)
			const int halt = o;
			if (S == 8)
			{
				put2l(0x48b9, &a);        // mov rcx, a
				put2(0x8911);             // mov [rcx], edx
				put2l(0x48b9, &b);        // mov rcx, b
				put2(0x8931);             // mov [rcx], esi
				put2l(0x48b9, &c);        // mov rcx, c
				put2(0x8939);             // mov [rcx], edi
				put2l(0x48b9, &d);        // mov rcx, d
				put2(0x8929);             // mov [rcx], ebp
				put2l(0x48b9, &f);        // mov rcx, f
				put2(0x8919);             // mov [rcx], ebx
				put4(0x4883c408);         // add rsp, 8
				put2(0x415f);             // pop r15
				put2(0x415e);             // pop r14
				put2(0x415d);             // pop r13
				put2(0x415c);             // pop r12
			}
			else
			{
				put2a(0x8915, &a);        // mov [a], edx
				put2a(0x8935, &b);        // mov [b], esi
				put2a(0x893d, &c);        // mov [c], edi
				put2a(0x892d, &d);        // mov [d], ebp
				put2a(0x891d, &f);        // mov [f], ebx
				put3(0x83c40c);           // add esp, 12
			}
			put1(0x5b);                 // pop ebx
			put1(0x5f);                 // pop edi
			put1(0x5e);                 // pop esi
			put1(0x5d);                 // pop ebp
			put1(0xc3);                 // ret

			// Code for the out instruction.
			// Store a=edx at outbuf[bufptr++]. If full, call flush1().
			const int outlabel = o;
			if (S == 8)
			{
				put2l(0x48b8, &outbuf[0]);// mov rax, outbuf.p
				put2l(0x49ba, &bufptr);   // mov r10, &bufptr
				put3(0x418b0a);           // mov rcx, [r10]
				put3(0x881408);           // mov [rax+rcx], dl
				put2(0xffc1);             // inc rcx
				put3(0x41890a);           // mov [r10], ecx
				put2a(0x81f9, outbuf.Length);  // cmp rcx, outbuf.Length
				put2(0x7403);             // jz L1
				put2(0x31c0);             // xor eax, eax
				put1(0xc3);               // ret

				put1(0x55);               // L1: push rbp ; call flush1(this)
				put1(0x57);               // push rdi
				put1(0x56);               // push rsi
				put1(0x52);               // push rdx
				put1(0x51);               // push rcx
				put3(0x4889e5);           // mov rbp, rsp
				put4(0x4883c570);         // add rbp, 112
#if defined(unix) && !defined(__CYGWIN__)
    put2l(0x48bf, this);      // mov rdi, this
#else  // Windows
				put2l(0x48b9, this);      // mov rcx, this
#endif
				put2l(0x49bb, &flush1);   // mov r11, &flush1
				put3(0x41ffd3);           // call r11
				put1(0x59);               // pop rcx
				put1(0x5a);               // pop rdx
				put1(0x5e);               // pop rsi
				put1(0x5f);               // pop rdi
				put1(0x5d);               // pop rbp
			}
			else
			{
				put1a(0xb8, &outbuf[0]);  // mov eax, outbuf.p
				put2a(0x8b0d, &bufptr);   // mov ecx, [bufptr]
				put3(0x881408);           // mov [eax+ecx], dl
				put2(0xffc1);             // inc ecx
				put2a(0x890d, &bufptr);   // mov [bufptr], ecx
				put2a(0x81f9, outbuf.Length);  // cmp ecx, outbuf.Length
				put2(0x7403);             // jz L1
				put2(0x31c0);             // xor eax, eax
				put1(0xc3);               // ret
				put3(0x83ec0c);           // L1: sub esp, 12
				put4(0x89542404);         // mov [esp+4], edx
				put3a(0xc70424, this);    // mov [esp], this
				put1a(0xb8, &flush1);     // mov eax, &flush1
				put2(0xffd0);             // call eax
				put4(0x8b542404);         // mov edx, [esp+4]
				put3(0x83c40c);           // add esp, 12
			}
			put1(0xc3);               // ret

			// Set it[i]=1 for each ZPAQL instruction reachable from the previous
			// instruction + 2 if reachable by a jump (or 3 if both).
			it[0] = 2;
			assert(hlen > 0 && hcomp[hlen - 1] == 0);  // ends with error
			do
			{
				done = 0;
				const int NONE = 0x80000000;
				for (int i = 0; i < hlen; ++i)
				{
					int op = hcomp[i];
					if (it[i])
					{
						int next1 = i + oplen(hcomp + i), next2 = NONE; // next and jump targets
						if (iserr(op)) next1 = NONE;  // error
						if (op == 56) next1 = NONE, next2 = 0;  // halt
						if (op == 255) next1 = NONE, next2 = hcomp[i + 1] + 256 * hcomp[i + 2]; // lj
						if (op == 39 || op == 47 || op == 63) next2 = i + 2 + (hcomp[i + 1] << 24 >> 24);// jt,jf,jmp
						if (op == 63) next1 = NONE;  // jmp
						if ((next2 < 0 || next2 >= hlen) && next2 != NONE) next2 = hlen - 1; // error
						if (next1 >= 0 && next1 < hlen && !(it[next1] & 1)) it[next1] |= 1, ++done;
						if (next2 >= 0 && next2 < hlen && !(it[next2] & 2)) it[next2] |= 2, ++done;
					}
				}
			} while (done > 0);

			// Set it[i] bits 2-3 to 4, 8, or 12 if a comparison
			//  (==, <, > respectively) does not need to save the result in f,
			// or if a conditional jump (jt, jf) does not need to read f.
			// This is true if a comparison is followed directly by a jt/jf,
			// the jt/jf is not a jump target, the byte before is not a jump
			// target (for a 2 byte comparison), and for the comparison instruction
			// if both paths after the jt/jf lead to another comparison or error
			// before another jt/jf. At most hlen steps are traced because after
			// that it must be an infinite loop.
			for (int i = 0; i < hlen; ++i)
			{
				const int op1 = hcomp[i]; // 216..239 = comparison
				const int i2 = i + 1 + (op1 % 8 == 7);  // address of next instruction
				const int op2 = hcomp[i2];  // 39,47 = jt,jf
				if (it[i] && op1 >= 216 && op1 < 240 && (op2 == 39 || op2 == 47)
					&& it[i2] == 1 && (i2 == i + 1 || it[i + 1] == 0))
				{
					int code = (op1 - 208) / 8 * 4; // 4,8,12 is ==,<,>
					it[i2] += code;  // OK to test CF, ZF instead of f
					for (int j = 0; j < 2 && code; ++j)
					{  // trace each path from i2
						int k = i2 + 2; // branch not taken
						if (j == 1) k = i2 + 2 + (hcomp[i2 + 1] << 24 >> 24);  // branch taken
						for (int l = 0; l < hlen && code; ++l)
						{  // trace at most hlen steps
							if (k < 0 || k >= hlen) break;  // out of bounds, pass
							const int op = hcomp[k];
							if (op == 39 || op == 47) code = 0;  // jt,jf, fail
							else if (op >= 216 && op < 240) break;  // ==,<,>, pass
							else if (iserr(op)) break;  // error, pass
							else if (op == 255) k = hcomp[k + 1] + 256 * hcomp[k + 2]; // lj
							else if (op == 63) k = k + 2 + (hcomp[k + 1] << 24 >> 24);  // jmp
							else if (op == 56) k = 0;  // halt
							else k = k + 1 + (op % 8 == 7);  // ordinary instruction
						}
					}
					it[i] += code;  // if > 0 then OK to not save flags in f (bl)
				}
			}

			// Start of run(): Save x86 and load ZPAQL registers
			const int start = o;
			assert(start >= 16);
			put1(0x55);          // push ebp/rbp
			put1(0x56);          // push esi/rsi
			put1(0x57);          // push edi/rdi
			put1(0x53);          // push ebx/rbx
			if (S == 8)
			{
				put2(0x4154);      // push r12
				put2(0x4155);      // push r13
				put2(0x4156);      // push r14
				put2(0x4157);      // push r15
				put4(0x4883ec08);  // sub rsp, 8
				put2l(0x48b8, &a); // mov rax, a
				put2(0x8b10);      // mov edx, [rax]
				put2l(0x48b8, &b); // mov rax, b
				put2(0x8b30);      // mov esi, [rax]
				put2l(0x48b8, &c); // mov rax, c
				put2(0x8b38);      // mov edi, [rax]
				put2l(0x48b8, &d); // mov rax, d
				put2(0x8b28);      // mov ebp, [rax]
				put2l(0x48b8, &f); // mov rax, f
				put2(0x8b18);      // mov ebx, [rax]
				put2l(0x49bc, &h[0]);   // mov r12, h
				put2l(0x49bd, &outbuf[0]); // mov r13, outbuf.p
				put2l(0x49be, &r[0]);   // mov r14, r
				put2l(0x49bf, &m[0]);   // mov r15, m
			}
			else
			{
				put3(0x83ec0c);    // sub esp, 12
				put2a(0x8b15, &a); // mov edx, [a]
				put2a(0x8b35, &b); // mov esi, [b]
				put2a(0x8b3d, &c); // mov edi, [c]
				put2a(0x8b2d, &d); // mov ebp, [d]
				put2a(0x8b1d, &f); // mov ebx, [f]
			}

			// Assemble in multiple passes until every byte of hcomp has a translation
			for (int istart = 0; istart < hlen; ++istart)
			{
				int inc = 0;
				for (int i = istart; i < hlen && it[i]; i += inc)
				{
					const int code = it[i];
					inc = oplen(hcomp + i);

					// If already assembled, then assemble a jump to it
					uint t;
					assert(it.Length > i);
					assert(i >= 0 && i < hlen);
					if (code >= 16)
					{
						if (i > istart)
						{
							int a = code - o;
							if (a > -120 && a < 120)
								put2(0xeb00 + ((a - 2) & 255)); // jmp short o
							else
								put1a(0xe9, a - 5);  // jmp near o
						}
						break;
					}

					// Else assemble the instruction at hcomp[i] to rcode[o]
					else
					{
						assert(i >= 0 && i < it.Length);
						assert(it[i] > 0 && it[i] < 16);
						assert(o >= 16);
						it[i] = o;
						++done;
						const int op = hcomp[i];
						const int arg = hcomp[i + 1] + ((op == 255) ? 256 * hcomp[i + 2] : 0);
						const int ddd = op / 8 % 8;
						const int sss = op % 8;

						// error instruction: return 1
						if (iserr(op))
						{
							put1a(0xb8, 1);         // mov eax, 1
							put1a(0xe9, halt - o - 4);  // jmp near halt
							continue;
						}

						// Load source *b, *c, *d, or hash (*b) into eax except:
						// {a,b,c,d}=*d, a{+,-,*,&,|,^,=,==,>,>}=*d: load address to eax
						// {a,b,c,d}={*b,*c}: load source into ddd
						if (op == 59 || (op >= 64 && op < 240 && op % 8 >= 4 && op % 8 < 7))
						{
							put2(0x89c0 + 8 * regcode[sss - 3 + (op == 59)]);  // mov eax, {esi,edi,ebp}
							const int sz = (sss == 6 ? hsize : msize) - 1;
							if (sz >= 128) put1a(0x25, sz);            // and eax, dword msize-1
							else put3(0x83e000 + sz);                  // and eax, byte msize-1
							const int move = (op >= 64 && op < 112); // = or else ddd is eax
							if (sss < 6)
							{ // ddd={a,b,c,d,*b,*c}
								if (S == 8) put5(0x410fb604 + 8 * move * regcode[ddd], 0x07);
								// movzx ddd, byte [r15+rax]
								else put3a(0x0fb680 + 8 * move * regcode[ddd], &m[0]);
								// movzx ddd, byte [m+eax]
							}
							else if ((0x06587000 >> (op / 8)) & 1)
							{// {*b,*c,*d,a/,a%,a&~,a<<,a>>}=*d
								if (S == 8) put4(0x418b0484);            // mov eax, [r12+rax*4]
								else put3a(0x8b0485, &h[0]);           // mov eax, [h+eax*4]
							}
						}

						// Load destination address *b, *c, *d or hashd (*d) into ecx
						if ((op >= 32 && op < 56 && op % 8 < 5) || (op >= 96 && op < 120) || op == 60)
						{
							put2(0x89c1 + 8 * regcode[op / 8 % 8 - 3 - (op == 60)]);// mov ecx,{esi,edi,ebp}
							const int sz = (ddd == 6 || op == 60 ? hsize : msize) - 1;
							if (sz >= 128) put2a(0x81e1, sz);   // and ecx, dword sz
							else put3(0x83e100 + sz);           // and ecx, byte sz
							if (op / 8 % 8 == 6 || op == 60)
							{ // *d
								if (S == 8) put4(0x498d0c8c);     // lea rcx, [r12+rcx*4]
								else put3a(0x8d0c8d, &h[0]);    // lea ecx, [ecx*4+h]
							}
							else
							{ // *b, *c
								if (S == 8) put4(0x498d0c0f);     // lea rcx, [r15+rcx]
								else put2a(0x8d89, &m[0]);      // lea ecx, [ecx+h]
							}
						}

						// Translate by opcode
						switch ((op / 8) & 31)
						{
							case 0:  // ddd = a
							case 1:  // ddd = b
							case 2:  // ddd = c
							case 3:  // ddd = d
								switch (sss)
								{
									case 0:  // ddd<>a (swap)
										put2(0x87d0 + regcode[ddd]);   // xchg edx, ddd
										break;
									case 1:  // ddd++
										put3(0x83c000 + 256 * regcode[ddd] + inc); // add ddd, inc
										break;
									case 2:  // ddd--
										put3(0x83e800 + 256 * regcode[ddd] + inc); // sub ddd, inc
										break;
									case 3:  // ddd!
										put2(0xf7d0 + regcode[ddd]);   // not ddd
										break;
									case 4:  // ddd=0
										put2(0x31c0 + 9 * regcode[ddd]); // xor ddd,ddd
										break;
									case 7:  // ddd=r n
										if (S == 8)
											put3a(0x418b86 + 8 * regcode[ddd], arg * 4); // mov ddd, [r14+n*4]
										else
											put2a(0x8b05 + 8 * regcode[ddd], (&r[arg]));//mov ddd, [r+n]
										break;
								}
								break;
							case 4:  // ddd = *b
							case 5:  // ddd = *c
								switch (sss)
								{
									case 0:  // ddd<>a (swap)
										put2(0x8611);                // xchg dl, [ecx]
										break;
									case 1:  // ddd++
										put3(0x800100 + inc);          // add byte [ecx], inc
										break;
									case 2:  // ddd--
										put3(0x802900 + inc);          // sub byte [ecx], inc
										break;
									case 3:  // ddd!
										put2(0xf611);                // not byte [ecx]
										break;
									case 4:  // ddd=0
										put2(0x31c0);                // xor eax, eax
										put2(0x8801);                // mov [ecx], al
										break;
									case 7:  // jt, jf
										{
											assert(code >= 0 && code < 16);
											 byte[,] jtab = new byte[2,4] {{5,4,2,7},{4,5,3,6}};
											 // jnz,je,jb,ja, jz,jne,jae,jbe
											if (code<4) put2(0x84db);    // test bl, bl
											if (arg>=128 && arg-257-i>=0 && o-it[arg - 257 - i]<120)

											  put2(0x7000+256*jtab[op == 47][code / 4]); // jx short 0
											else

											  put2a(0x0f80+jtab[op == 47][code / 4], 0); // jx near 0
											break;
										  }
									}
								 break;
          case 6:  // ddd = *d
            switch(sss) {
              case 0:  // ddd<>a (swap)

				put2(0x8711);             // xchg edx, [ecx]
                break;
              case 1:  // ddd++

				put3(0x830100+inc);       // add dword [ecx], inc
                break;
              case 2:  // ddd--

				put3(0x832900+inc);       // sub dword [ecx], inc
                break;
              case 3:  // ddd!

				put2(0xf711);             // not dword [ecx]
                break;
              case 4:  // ddd=0

				put2(0x31c0);             // xor eax, eax

				put2(0x8901);             // mov [ecx], eax
                break;
              case 7:  // ddd=r n
                if (S==8)

				  put3a(0x418996, arg*4); // mov [r14+n*4], edx
                else

				  put2a(0x8915, &r[arg]); // mov [r+n], edx
                break;
            }
            break;
          case 7:  // special
            switch(op) {
              case 56: // halt

				put2(0x31c0);             // xor eax, eax  ; return 0

				put1a(0xe9, halt-o-4);    // jmp near halt
                break;
              case 57:  // out

				put1a(0xe8, outlabel-o-4);// call outlabel

				put3(0x83f800);           // cmp eax, 0  ; returned error code

				put2(0x7405);             // je L1:

				put1a(0xe9, halt-o-4);    // jmp near halt ; L1:
                break;
              case 59:  // hash: a = (a + *b + 512) * 773

				put3a(0x8d8410, 512);     // lea edx, [eax+edx+512]

				put2a(0x69d0, 773);       // imul edx, eax, 773
                break;
              case 60:  // hashd: *d = (*d + a + 512) * 773

				put2(0x8b01);             // mov eax, [ecx]

				put3a(0x8d8410, 512);     // lea eax, [eax+edx+512]

				put2a(0x69c0, 773);       // imul eax, eax, 773

				put2(0x8901);             // mov [ecx], eax
                break;
              case 63:  // jmp

				put1a(0xe9, 0);           // jmp near 0 (fill in target later)
                break;
            }
            break;
          case 8:   // a=
          case 9:   // b=
          case 10:  // c=
          case 11:  // d=
            if (sss==7)  // n

			  put1a(0xb8+regcode[ddd], arg);         // mov ddd, n
            else if (sss==6) { // *d
              if (S==8)

				put4(0x418b0484+(regcode[ddd]<<11)); // mov ddd, [r12+rax*4]
              else

				put3a(0x8b0485+(regcode[ddd]<<11),&h[0]);// mov ddd, [h+eax*4]
            }
            else if (sss<4) // a, b, c, d

			  put2(0x89c0+regcode[ddd]+8* regcode[sss]);// mov ddd,sss
            break;
          case 12:  // *b=
          case 13:  // *c=
            if (sss==7) put3(0xc60100+arg);          // mov byte [ecx], n
            else if (sss==0) put2(0x8811);           // mov byte [ecx], dl
            else {
              if (sss<4) put2(0x89c0+8* regcode[sss]);// mov eax, sss

			  put2(0x8801);                          // mov byte [ecx], al
            }
            break;
          case 14:  // *d=
            if (sss<7) put2(0x8901+8* regcode[sss]);  // mov [ecx], sss
            else put2a(0xc701, arg);                 // mov dword [ecx], n
            break;
          case 15: break; // not used
          case 16:  // a+=
            if (sss==6) {
              if (S==8) put4(0x41031484);            // add edx, [r12+rax*4]
              else put3a(0x031485, &h[0]);           // add edx, [h+eax*4]
            }
            else if (sss<7) put2(0x01c2+8* regcode[sss]);// add edx, sss
            else if (arg>=128) put2a(0x81c2, arg);   // add edx, n
            else put3(0x83c200+arg);                 // add edx, byte n
            break;
          case 17:  // a-=
            if (sss==6) {
              if (S==8) put4(0x412b1484);            // sub edx, [r12+rax*4]
              else put3a(0x2b1485, &h[0]);           // sub edx, [h+eax*4]
            }
            else if (sss<7) put2(0x29c2+8* regcode[sss]);// sub edx, sss
            else if (arg>=128) put2a(0x81ea, arg);   // sub edx, n
            else put3(0x83ea00+arg);                 // sub edx, byte n
            break;
          case 18:  // a*=
            if (sss==6) {
              if (S==8) put5(0x410faf14,0x84);       // imul edx, [r12+rax*4]
              else put4a(0x0faf1485, &h[0]);         // imul edx, [h+eax*4]
            }
            else if (sss<7) put3(0x0fafd0+regcode[sss]);// imul edx, sss
            else if (arg>=128) put2a(0x69d2, arg);   // imul edx, n
            else put3(0x6bd200+arg);                 // imul edx, byte n
            break;
          case 19:  // a/=
          case 20:  // a%=
            if (sss<7) put2(0x89c1+8* regcode[sss]);  // mov ecx, sss
            else put1a(0xb9, arg);                   // mov ecx, n

			put2(0x85c9);                            // test ecx, ecx

			put3(0x0f44d1);                          // cmovz edx, ecx

			put2(0x7408-2*(op/8==20));               // jz (over rest)

			put2(0x89d0);                            // mov eax, edx

			put2(0x31d2);                            // xor edx, edx

			put2(0xf7f1);                            // div ecx
            if (op/8==19) put2(0x89c2);              // mov edx, eax
            break;
          case 21:  // a&=
            if (sss==6) {
              if (S==8) put4(0x41231484);            // and edx, [r12+rax*4]
              else put3a(0x231485, &h[0]);           // and edx, [h+eax*4]
            }
            else if (sss<7) put2(0x21c2+8* regcode[sss]);// and edx, sss
            else if (arg>=128) put2a(0x81e2, arg);   // and edx, n
            else put3(0x83e200+arg);                 // and edx, byte n
            break;
          case 22:  // a&~
            if (sss==7) {
              if (arg<128) put3(0x83e200+(~arg&255));// and edx, byte ~n
              else put2a(0x81e2, ~arg);              // and edx, ~n
            }
            else {
              if (sss<4) put2(0x89c0+8* regcode[sss]);// mov eax, sss

			  put2(0xf7d0);                          // not eax

			  put2(0x21c2);                          // and edx, eax
            }
            break;
          case 23:  // a|=
            if (sss==6) {
              if (S==8) put4(0x410b1484);            // or edx, [r12+rax*4]
              else put3a(0x0b1485, &h[0]);           // or edx, [h+eax*4]
            }
            else if (sss<7) put2(0x09c2+8* regcode[sss]);// or edx, sss
            else if (arg>=128) put2a(0x81ca, arg);   // or edx, n
            else put3(0x83ca00+arg);                 // or edx, byte n
            break;
          case 24:  // a^=
            if (sss==6) {
              if (S==8) put4(0x41331484);            // xor edx, [r12+rax*4]
              else put3a(0x331485, &h[0]);           // xor edx, [h+eax*4]
            }
            else if (sss<7) put2(0x31c2+8* regcode[sss]);// xor edx, sss
            else if (arg>=128) put2a(0x81f2, arg);   // xor edx, byte n
            else put3(0x83f200+arg);                 // xor edx, n
            break;
          case 25:  // a<<=
          case 26:  // a>>=
            if (sss==7)  // sss = n

			  put3(0xc1e200+8*256*(op/8==26)+arg);   // shl/shr n
            else {

			  put2(0x89c1+8*regcode[sss]);           // mov ecx, sss

			  put2(0xd3e2+8*(op/8==26));             // shl/shr edx, cl
            }
            break;
          case 27:  // a==
          case 28:  // a<
          case 29:  // a>
            if (sss==6) {
              if (S==8) put4(0x413b1484);            // cmp edx, [r12+rax*4]
              else put3a(0x3b1485, &h[0]);           // cmp edx, [h+eax*4]
            }
            else if (sss==7)  // sss = n

			  put2a(0x81fa, arg);                    // cmp edx, dword n
            else

			  put2(0x39c2+8*regcode[sss]);           // cmp edx, sss
            if (code<4) {
              if (op/8==27) put3(0x0f94c3);          // setz bl
              if (op/8==28) put3(0x0f92c3);          // setc bl
              if (op/8==29) put3(0x0f97c3);          // seta bl
            }
            break;
          case 30:  // not used
          case 31:  // 255 = lj
            if (op==255) put1a(0xe9, 0);             // jmp near
            break;
        }
      }
    }
  }

		  // Finish first pass
		  const int rsize = o;
		  if (o>rcode_size) return rsize;

		  // Fill in jump addresses (second pass)
		  for (int i=0; i<hlen; ++i) {
			if (it[i]<16) continue;
			int op = hcomp[i];
			if (op==39 || op==47 || op==63 || op==255) {  // jt, jf, jmp, lj
			  int target = hcomp[i + 1];
			  if (op==255) target+=hcomp[i + 2]*256;  // lj
			  else {
				if (target>=128) target-=256;
				target+=i+2;
			  }
			  if (target<0 || target>=hlen) target=hlen-1;  // runtime ZPAQL error
			  o=it[i];

			  assert(o>=16 && o<rcode_size);
			  if ((op==39 || op==47) && rcode[o]==0x84) o+=2;  // jt, jf . skip test

			  assert(o>=16 && o<rcode_size);
			  if (rcode[o]==0x0f) ++o;  // first byte of jz near, jnz near

			  assert(o<rcode_size);
		op=rcode[o++];  // x86 opcode
			  target=it[target]-o;
			  if ((op>=0x72 && op<0x78) || op==0xeb) {  // jx, jmp short
				--target;
				if (target<-128 || target>127)

				  error("Cannot code x86 short jump");

				assert(o<rcode_size);
		rcode[o]=target&255;
			  }
			  else if ((op>=0x82 && op<0x88) || op==0xe9) // jx, jmp near
			  {
				target-=4;

				puta(target);
			  }
			  else assert(false);  // not a x86 jump
			}
		  }

		  // Jump to start
		  o=0;
		  put1a(0xe9, start-5);  // jmp near start
		  return rsize;
		}

		private void init(int hbits, int mbits) // initialize H and M sizes
		{
			assert(header.Length > 0);
			assert(cend >= 7);
			assert(hbegin >= cend + 128);
			assert(hend >= hbegin);
			assert(hend < header.Length - 130);
			assert(header[0] + 256 * header[1] == cend - 2 + hend - hbegin);
			assert(bufptr == 0);
			assert(outbuf.Length > 0);
			if (hbits > 32) error("H too big");
			if (mbits > 32) error("M too big");
			Array.Resize(ref h, 1, hbits);
			Array.Resize(ref m, 1, mbits);
			Array.Resize(ref r, 256);
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
				case 220: f = (a == uint(m(b))); break; // A==*B
				case 221: f = (a == uint(m(c))); break; // A==*C
				case 222: f = (a == h(d)); break; // A==*D
				case 223: f = (a == uint(header[pc++])); break; // A== N
				case 224: f = 0; break; // A<A
				case 225: f = (a < b); break; // A<B
				case 226: f = (a < c); break; // A<C
				case 227: f = (a < d); break; // A<D
				case 228: f = (a < uint(m(b))); break; // A<*B
				case 229: f = (a < uint(m(c))); break; // A<*C
				case 230: f = (a < h(d)); break; // A<*D
				case 231: f = (a < uint(header[pc++])); break; // A< N
				case 232: f = 0; break; // A>A
				case 233: f = (a > b); break; // A>B
				case 234: f = (a > c); break; // A>C
				case 235: f = (a > d); break; // A>D
				case 236: f = (a > uint(m(b))); break; // A>*B
				case 237: f = (a > uint(m(c))); break; // A>*C
				case 238: f = (a > h(d)); break; // A>*D
				case 239: f = (a > uint(header[pc++])); break; // A> N
				case 255: if ((pc = hbegin + header[pc] + 256 * header[pc + 1]) >= hend) err(); break;//LJ
				default: err();
			}
			return 1;
		}

		private void run0(uint input) // default run() if not JIT
		{
			assert(cend > 6);
			assert(hbegin >= cend + 128);
			assert(hend >= hbegin);
			assert(hend < header.Length - 130);
			assert(m.Length > 0);
			assert(h.Length > 0);
			assert(header[0] + 256 * header[1] == cend + hend - hbegin - 2);
			pc = hbegin;
			a = input;
			while (execute()) ;
		}

		private void div(uint x)
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

		private void mod(uint x)
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

		private void swap(ref uint x)
		{
			a ^= x;
			x ^= a;
			a ^= x;
		}

		private void swap(ref byte x)
		{
			a ^= x;
			x = (byte)(x ^ a);
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

		// Called by out
		static int flush1(ZPAQL z)
		{
			try
			{
				z.flush();
				return 0;
			}
			catch (SystemException /*std::bad_alloc&x*/)
			{
				return 2;
			}
			catch
			{
				return 3;
			}
		}

		// return true if op is an undefined ZPAQL instruction
		static bool iserr(int op)
		{
			return op == 0 || (op >= 120 && op <= 127) || (op >= 240 && op <= 254)
			  || op == 58 || (op < 64 && (op % 8 == 5 || op % 8 == 6));
		}

		// Return length of ZPAQL instruction at hcomp[0]. Assume 0 padding at end.
		// A run of identical ++ or -- is counted as 1 instruction.
		static int oplen(byte[] hcomp, int hcompPtr)
		{
			if (hcompPtr == 255) return 3;
			if (hcompPtr % 8 == 7) return 2;
			if (hcompPtr < 51 && (hcompPtr % 8 - 1) / 2 == 0)
			{  // ++ or -- opcode
				int i;
				for (i = 1; i < 127 && hcomp[i] == hcomp[0]; ++i) ;
				return i;
			}
			return 1;
		}

		// Write k bytes of x to rcode[o++] MSB first
		static void put(byte* rcode, int n, int& o, uint x, int k)
		{
			while (k-- > 0)
			{
				if (o < n) rcode[o] = (x >> (k * 8)) & 255;
				++o;
			}
		}

		// Write 4 bytes of x to rcode[o++] LSB first
		static void put4lsb(byte* rcode, int n, int& o, uint x)
		{
			for (int k = 0; k < 4; ++k)
			{
				if (o < n) rcode[o] = (x >> (k * 8)) & 255;
				++o;
			}
		}

		// Write a 1-4 byte x86 opcode without or with an 4 byte operand
		// to rcode[o...]
#define put1(x) put(rcode, rcode_size, o, (x), 1)
#define put2(x) put(rcode, rcode_size, o, (x), 2)
#define put3(x) put(rcode, rcode_size, o, (x), 3)
#define put4(x) put(rcode, rcode_size, o, (x), 4)
#define put5(x,y) put4(x), put1(y)
#define put6(x,y) put4(x), put2(y)
#define put4r(x) put4lsb(rcode, rcode_size, o, x)
#define puta(x) t=uint(size_t(x)), put4r(t)
#define put1a(x,y) put1(x), puta(y)
#define put2a(x,y) put2(x), puta(y)
#define put3a(x,y) put3(x), puta(y)
#define put4a(x,y) put4(x), puta(y)
#define put5a(x,y,z) put4(x), put1(y), puta(z)
#define put2l(x,y) put2(x), t=uint(size_t(y)), put4r(t), \
		t=uint(size_t(y)>>(S*4)), put4r(t)
	}
}
