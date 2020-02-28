
// Functions and kernel for mining with sha512

int compare_uint256(uint64_t* x, uint64_t* y)
{
#pragma unroll
    for (int i = 3; i >= 0; i--) {

        uint32_t xh = x[i] >> 32;
        uint32_t xl = x[i];

        uint32_t yh = y[i] >> 32;
        uint32_t yl = y[i];

        if (xh < yh)
            return -1;
        if (xh > yh)
            return 1;
        if (xl < yl)
            return -1;
        if (xl > yl)
            return 1;
    }

    return 0;
}

void pad_buffer(uint64_t* w, int lengthBytes) {

    int lw = lengthBytes / 8;
    int lb = lengthBytes * 8;
    w[lw] = 0x8000000000000000UL;

#pragma unroll
    for (int i = lw + 1; i < 15; i++)
        w[i] = 0;

    w[15] = lb;
}

void sha512_compute_hash(uint64_t* w, int iterations)
{
    uint64_t a, b, c, d, e, f, g, h;
    uint64_t t;

    for (size_t j = 0; j < iterations; j++) {

        a = H0;
        b = H1;
        c = H2;
        d = H3;
        e = H4;
        f = H5;
        g = H6;
        h = H7;

#pragma unroll
        for (int i = 0; i < 16; i++) {
            t = k[i] + w[i] + h + Sigma1(e) + Ch(e, f, g);

            h = g;
            g = f;
            f = e;
            e = d + t;
            t = t + Maj(a, b, c) + Sigma0(a);
            d = c;
            c = b;
            b = a;
            a = t;
        }

#pragma unroll
        for (int i = 16; i < 80; i++) {
            w[i & 15] = sigma1(w[(i - 2) & 15]) + sigma0(w[(i - 15) & 15]) + w[(i - 16) & 15] + w[(i - 7) & 15];
            t = k[i] + w[i & 15] + h + Sigma1(e) + Ch(e, f, g);

            h = g;
            g = f;
            f = e;
            e = d + t;
            t = t + Maj(a, b, c) + Sigma0(a);
            d = c;
            c = b;
            b = a;
            a = t;
        }

        w[0] = a + H0;
        w[1] = b + H1;
        w[2] = c + H2;
        w[3] = d + H3;
        w[4] = e + H4;
        w[5] = f + H5;
        w[6] = g + H6;
        w[7] = h + H7;

        pad_buffer(w, 64);
    }
}

__kernel
void kernel_find_pow(
    __global uint64_t* header
    , __global uint64_t* bits
    , uint32_t ns
    , __global int* out)
 {
    uint64_t wh[16];
    uint64_t wht[4];
    uint64_t wb[4];

#pragma unroll
    for (int i = 0; i < 10; i++)
        wh[i] = SWAP64(header[i]);

    uint32_t n = ns + get_global_id(0);
    wh[9] += ((n >> 24) | ((n << 8) & 0x00FF0000) | ((n >> 8) & 0x0000FF00) | (n << 24));

    pad_buffer(wh, 80);

    sha512_compute_hash(wh, 2);

    for (int i = 0; i < 4; i++)
        wht[i] = SWAP64(wh[i]);

#pragma unroll
    for (int i = 0; i < 4; i++)
        wb[i] = bits[i];

    if (compare_uint256(wht, wb) <= 0)
        out[0] = n;
}
