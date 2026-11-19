/* GTCRN streaming inference, after stream/gtcrn_stream.py of
 * https://github.com/Xiaobin-Rong/gtcrn (MIT, see LICENSE). */
#include "gtcrn.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>
#ifdef __SSE__
#include <xmmintrin.h>
#endif

#include "gtcrn_weights.h"

#define NFFT 512
#define HOP 256
#define NB 257   /* STFT bins */
#define ERB1 65  /* bins kept as they are */
#define ERB2 64  /* ERB bands for bins 65..256 */
#define NE 129   /* ERB1 + ERB2 */
#define CH 16    /* channels inside the network */
#define W 33     /* frequency width inside the network */
#define W1 65    /* width after the first encoder conv */
#define MAXDIL 5 /* largest time dilation of the GTConv blocks */

#define FIR 129     /* 48 <-> 16 kHz low-pass taps */
#define FIR_HZ 7000 /* low-pass cutoff */
#define HB_LO 128   /* bins 128..191 (4..6 kHz) set the high band gain */
#define HB_HI 192
#define GTCRN_PI 3.14159265358979323846

struct gt_w
{
    const float *pc1_w, *pc1_b, *a1, *dc_w, *dc_b, *a2, *pc2_w, *pc2_b;
    const float *wih, *whh, *bih, *bhh, *fcw, *fcb;
    int dil;
};

#define GT(n, d)                                                                             \
    {                                                                                        \
        gt##n##_pc1_w, gt##n##_pc1_b, gt##n##_a1, gt##n##_dc_w, gt##n##_dc_b, gt##n##_a2,    \
            gt##n##_pc2_w, gt##n##_pc2_b, gt##n##_tra_wih, gt##n##_tra_whh, gt##n##_tra_bih, \
            gt##n##_tra_bhh, gt##n##_tra_fcw, gt##n##_tra_fcb, d                             \
    }

/* Encoder blocks 0..2, decoder blocks 3..5. */
static const struct gt_w GTW[6] = {GT(0, 1), GT(1, 2), GT(2, 5), GT(3, 5), GT(4, 2), GT(5, 1)};

struct gru_w
{
    const float *wih, *whh, *bih, *bhh;
};

struct dp_w
{
    struct gru_w intra[2][2]; /* [group][forward, backward] */
    struct gru_w inter[2];    /* [group] */
    const float *intra_fcw, *intra_fcb, *intra_lnw, *intra_lnb;
    const float *inter_fcw, *inter_fcb, *inter_lnw, *inter_lnb;
};

#define GRU(p)                             \
    {                                      \
        p##_wih, p##_whh, p##_bih, p##_bhh \
    }
#define DP(n)                                                                                             \
    {                                                                                                     \
        {{GRU(dp##n##_intra1_f), GRU(dp##n##_intra1_b)}, {GRU(dp##n##_intra2_f), GRU(dp##n##_intra2_b)}}, \
            {GRU(dp##n##_inter1), GRU(dp##n##_inter2)}, dp##n##_intra_fcw, dp##n##_intra_fcb,             \
            dp##n##_intra_lnw, dp##n##_intra_lnb, dp##n##_inter_fcw, dp##n##_inter_fcb,                   \
            dp##n##_inter_lnw, dp##n##_inter_lnb                                                          \
    }

static const struct dp_w DPW[2] = {DP(0), DP(1)};

struct gtcrn
{
    /* Network state */
    float cache[6][2 * MAXDIL][CH * W]; /* depthwise conv input history, oldest first */
    float tra_h[6][16];
    float inter_h[2][W * CH];
    float erb[ERB2][NB - ERB1];

    /* 16 kHz STFT */
    float win[NFFT];
    float cos_t[NFFT / 2], sin_t[NFFT / 2];
    float frame[NFFT]; /* last NFFT input samples */
    float ola[NFFT];
    int fill;         /* new samples in frame since the last hop */
    float out16[HOP]; /* enhanced samples of the last hop */
    float ref16[HOP]; /* the same samples before enhancement */
    int out_pos;

    /* 48 kHz side */
    float fir[FIR];
    float xin[FIR];                            /* 48 kHz input history */
    float up[FIR / 3 + 1], upref[FIR / 3 + 1]; /* 16 kHz histories for the up-samplers */
    int phase;
    float xd[GTCRN_DELAY]; /* input delay line */
    int xd_pos;
    float hb_gain, hb_target;
};

static inline float sigm(float x)
{
    return 1.0f / (1.0f + expf(-x));
}

static inline float prelu(float x, float a)
{
    return x >= 0.0f ? x : a * x;
}

/* ERB filter bank as in gtcrn.py ERB.erb_filter_banks (numpy rounds half to even, so does rint). */
static void erb_init(float erb[ERB2][NB - ERB1])
{
    double lo = 21.4 * log10(0.00437 * (ERB1 / (double)NFFT * 16000.0) + 1.0);
    double hi = 21.4 * log10(0.00437 * 8000.0 + 1.0);
    double step = (hi - lo) / (ERB2 - 1);
    int bins[ERB2];
    for (int i = 0; i < ERB2; i++)
    {
        double e = i == ERB2 - 1 ? hi : i * step + lo;
        bins[i] = (int)rint((pow(10.0, e / 21.4) - 1.0) / 0.00437 / 16000.0 * NFFT);
    }
    static float full[ERB2][NB];
    memset(full, 0, sizeof(full));
    for (int k = bins[0]; k < bins[1]; k++)
        full[0][k] = (float)((bins[1] - k + 1e-12) / (bins[1] - bins[0] + 1e-12));
    for (int i = 0; i < ERB2 - 2; i++)
    {
        for (int k = bins[i]; k < bins[i + 1]; k++)
            full[i + 1][k] = (float)((k - bins[i] + 1e-12) / (bins[i + 1] - bins[i] + 1e-12));
        for (int k = bins[i + 1]; k < bins[i + 2]; k++)
            full[i + 1][k] = (float)((bins[i + 2] - k + 1e-12) / (bins[i + 2] - bins[i + 1] + 1e-12));
    }
    for (int k = bins[ERB2 - 2]; k <= bins[ERB2 - 1]; k++)
        full[ERB2 - 1][k] = 1.0f - full[ERB2 - 2][k];
    for (int j = 0; j < ERB2; j++)
        for (int k = 0; k < NB - ERB1; k++)
            erb[j][k] = fabsf(full[j][ERB1 + k]);
}

/* PyTorch GRU cell, gates r, z, n. nh <= 16. */
static void gru_cell(const struct gru_w *w, int ni, int nh, const float *x, float *h)
{
    float gi[48], gh[48];
    for (int g = 0; g < 3 * nh; g++)
    {
        float a = w->bih[g], b = w->bhh[g];
        for (int i = 0; i < ni; i++)
            a += w->wih[g * ni + i] * x[i];
        for (int j = 0; j < nh; j++)
            b += w->whh[g * nh + j] * h[j];
        gi[g] = a;
        gh[g] = b;
    }
    for (int j = 0; j < nh; j++)
    {
        float r = sigm(gi[j] + gh[j]);
        float z = sigm(gi[nh + j] + gh[nh + j]);
        float n = tanhf(gi[2 * nh + j] + r * gh[2 * nh + j]);
        h[j] = (1.0f - z) * n + z * h[j];
    }
}

/* Conv2d, kernel (1,5), stride (1,2), padding (0,2), PReLU. w is [out][in/groups][5]. */
static void conv_down(const float *w, const float *b, float a, int cin, int cout, int groups, int fin,
                      const float *x, float *y)
{
    int fout = (fin - 1) / 2 + 1, gin = cin / groups, gout = cout / groups;
    for (int o = 0; o < cout; o++)
    {
        int g = o / gout;
        for (int fo = 0; fo < fout; fo++)
        {
            float acc = b[o];
            for (int i = 0; i < gin; i++)
            {
                const float *xi = x + (g * gin + i) * fin, *wk = w + (o * gin + i) * 5;
                for (int k = 0; k < 5; k++)
                {
                    int fi = 2 * fo + k - 2;
                    if (fi >= 0 && fi < fin)
                        acc += wk[k] * xi[fi];
                }
            }
            y[o * fout + fo] = prelu(acc, a);
        }
    }
}

/* ConvTranspose2d, kernel (1,5), stride (1,2), padding (0,2). w is [in][out/groups][5]. */
static void conv_up(const float *w, const float *b, int cin, int cout, int groups, int fin, const float *x,
                    float *y)
{
    int fout = 2 * fin - 1, gin = cin / groups, gout = cout / groups;
    for (int o = 0; o < cout; o++)
        for (int fo = 0; fo < fout; fo++)
            y[o * fout + fo] = b[o];
    for (int i = 0; i < cin; i++)
    {
        int g = i / gin;
        for (int j = 0; j < gout; j++)
        {
            const float *wk = w + (i * gout + j) * 5;
            float *yo = y + (g * gout + j) * fout;
            for (int fi = 0; fi < fin; fi++)
            {
                float v = x[i * fin + fi];
                for (int k = 0; k < 5; k++)
                {
                    int fo = 2 * fi + k - 2;
                    if (fo >= 0 && fo < fout)
                        yo[fo] += wk[k] * v;
                }
            }
        }
    }
}

/* Grouped temporal conv block (StreamGTConvBlock) on CH x W. */
static void gt_block(const struct gt_w *w, float cache[2 * MAXDIL][CH * W], float *tra_h, const float *x,
                     float *y)
{
    int d = w->dil;
    float sfe[24 * W], h[CH * W], hd[CH * W], h2[8 * W], z[8], at[8];

    for (int c = 0; c < 8; c++)
        for (int j = 0; j < 3; j++)
            for (int f = 0; f < W; f++)
            {
                int ff = f + j - 1;
                sfe[(c * 3 + j) * W + f] = ff >= 0 && ff < W ? x[c * W + ff] : 0.0f;
            }
    for (int o = 0; o < CH; o++)
        for (int f = 0; f < W; f++)
        {
            float acc = w->pc1_b[o];
            for (int i = 0; i < 24; i++)
                acc += w->pc1_w[o * 24 + i] * sfe[i * W + f];
            h[o * W + f] = prelu(acc, w->a1[0]);
        }

    /* Depthwise (3,3) conv, time dilation d: taps at t-2d, t-d and t. */
    const float *taps[3] = {cache[0], cache[d], h};
    for (int c = 0; c < CH; c++)
        for (int f = 0; f < W; f++)
        {
            float acc = w->dc_b[c];
            for (int kt = 0; kt < 3; kt++)
                for (int kf = 0; kf < 3; kf++)
                {
                    int ff = f + kf - 1;
                    if (ff >= 0 && ff < W)
                        acc += w->dc_w[c * 9 + kt * 3 + kf] * taps[kt][c * W + ff];
                }
            hd[c * W + f] = prelu(acc, w->a2[0]);
        }
    memmove(cache[0], cache[1], (size_t)(2 * d - 1) * CH * W * sizeof(float));
    memcpy(cache[2 * d - 1], h, sizeof(h));

    for (int o = 0; o < 8; o++)
        for (int f = 0; f < W; f++)
        {
            float acc = w->pc2_b[o];
            for (int i = 0; i < CH; i++)
                acc += w->pc2_w[o * CH + i] * hd[i * W + f];
            h2[o * W + f] = acc;
        }

    /* Temporal recurrent attention: GRU over the mean power per channel. */
    for (int c = 0; c < 8; c++)
    {
        float s = 0.0f;
        for (int f = 0; f < W; f++)
            s += h2[c * W + f] * h2[c * W + f];
        z[c] = s / W;
    }
    struct gru_w g = {w->wih, w->whh, w->bih, w->bhh};
    gru_cell(&g, 8, 16, z, tra_h);
    for (int c = 0; c < 8; c++)
    {
        float acc = w->fcb[c];
        for (int j = 0; j < 16; j++)
            acc += w->fcw[c * 16 + j] * tra_h[j];
        at[c] = sigm(acc);
    }

    for (int c = 0; c < 8; c++)
        for (int f = 0; f < W; f++)
        {
            y[2 * c * W + f] = h2[c * W + f] * at[c];
            y[(2 * c + 1) * W + f] = x[(8 + c) * W + f];
        }
}

static void layer_norm(float v[W][CH], const float *gw, const float *gb)
{
    double mean = 0.0, var = 0.0;
    for (int f = 0; f < W; f++)
        for (int c = 0; c < CH; c++)
            mean += v[f][c];
    mean /= W * CH;
    for (int f = 0; f < W; f++)
        for (int c = 0; c < CH; c++)
            var += (v[f][c] - mean) * (v[f][c] - mean);
    float inv = (float)(1.0 / sqrt(var / (W * CH) + 1e-8));
    for (int f = 0; f < W; f++)
        for (int c = 0; c < CH; c++)
            v[f][c] = (float)(v[f][c] - mean) * inv * gw[f * CH + c] + gb[f * CH + c];
}

static void linear(const float *fw, const float *fb, const float in[W][CH], float out[W][CH])
{
    for (int f = 0; f < W; f++)
        for (int o = 0; o < CH; o++)
        {
            float acc = fb[o];
            for (int i = 0; i < CH; i++)
                acc += fw[o * CH + i] * in[f][i];
            out[f][o] = acc;
        }
}

/* Grouped dual-path RNN: bidirectional GRUs across frequency, then GRUs in time. */
static void dp_block(const struct dp_w *w, float *inter_h, float *x)
{
    float xt[W][CH], y[W][CH], z[W][CH];
    for (int c = 0; c < CH; c++)
        for (int f = 0; f < W; f++)
            xt[f][c] = x[c * W + f];

    for (int g = 0; g < 2; g++)
    {
        float h[4] = {0};
        for (int f = 0; f < W; f++)
        {
            gru_cell(&w->intra[g][0], 8, 4, &xt[f][g * 8], h);
            memcpy(&y[f][g * 8], h, sizeof(h));
        }
        memset(h, 0, sizeof(h));
        for (int f = W - 1; f >= 0; f--)
        {
            gru_cell(&w->intra[g][1], 8, 4, &xt[f][g * 8], h);
            memcpy(&y[f][g * 8 + 4], h, sizeof(h));
        }
    }
    linear(w->intra_fcw, w->intra_fcb, (const float(*)[CH])y, z);
    layer_norm(z, w->intra_lnw, w->intra_lnb);
    for (int f = 0; f < W; f++)
        for (int c = 0; c < CH; c++)
            xt[f][c] += z[f][c];

    for (int f = 0; f < W; f++)
        for (int g = 0; g < 2; g++)
        {
            float *h = inter_h + f * CH + g * 8;
            gru_cell(&w->inter[g], 8, 8, &xt[f][g * 8], h);
            memcpy(&y[f][g * 8], h, 8 * sizeof(float));
        }
    linear(w->inter_fcw, w->inter_fcb, (const float(*)[CH])y, z);
    layer_norm(z, w->inter_lnw, w->inter_lnb);
    for (int c = 0; c < CH; c++)
        for (int f = 0; f < W; f++)
            x[c * W + f] = xt[f][c] + z[f][c];
}

/* One 16 kHz STFT frame (257 bins, re/im interleaved) through the network. */
static void gtcrn_frame(gtcrn *st, const float *spec, float *out)
{
    float feat[3][NB], e[3][NE], s9[9 * NE];
    float en0[CH * W1], en1[CH * W], en[3][CH * W], x[CH * W], t[CH * W];
    float d0[CH * W1], d1[2 * NE], m[2][NB];

    for (int k = 0; k < NB; k++)
    {
        float re = spec[2 * k], im = spec[2 * k + 1];
        feat[0][k] = sqrtf(re * re + im * im + 1e-12f);
        feat[1][k] = re;
        feat[2][k] = im;
    }
    for (int c = 0; c < 3; c++)
    {
        memcpy(e[c], feat[c], ERB1 * sizeof(float));
        for (int j = 0; j < ERB2; j++)
        {
            float acc = 0.0f;
            for (int k = 0; k < NB - ERB1; k++)
                acc += st->erb[j][k] * feat[c][ERB1 + k];
            e[c][ERB1 + j] = acc;
        }
    }
    for (int c = 0; c < 3; c++)
        for (int j = 0; j < 3; j++)
            for (int f = 0; f < NE; f++)
            {
                int ff = f + j - 1;
                s9[(c * 3 + j) * NE + f] = ff >= 0 && ff < NE ? e[c][ff] : 0.0f;
            }

    conv_down(en0_w, en0_b, en0_a[0], 9, CH, 1, NE, s9, en0);
    conv_down(en1_w, en1_b, en1_a[0], CH, CH, 2, W1, en0, en1);
    gt_block(&GTW[0], st->cache[0], st->tra_h[0], en1, en[0]);
    gt_block(&GTW[1], st->cache[1], st->tra_h[1], en[0], en[1]);
    gt_block(&GTW[2], st->cache[2], st->tra_h[2], en[1], en[2]);

    memcpy(x, en[2], sizeof(x));
    dp_block(&DPW[0], st->inter_h[0], x);
    dp_block(&DPW[1], st->inter_h[1], x);

    for (int b = 0; b < 3; b++)
    {
        for (int i = 0; i < CH * W; i++)
            t[i] = x[i] + en[2 - b][i];
        gt_block(&GTW[3 + b], st->cache[3 + b], st->tra_h[3 + b], t, x);
    }
    for (int i = 0; i < CH * W; i++)
        t[i] = x[i] + en1[i];
    conv_up(de0_w, de0_b, CH, CH, 2, W, t, d0);
    for (int i = 0; i < CH * W1; i++)
        d0[i] = prelu(d0[i], de0_a[0]) + en0[i];
    conv_up(de1_w, de1_b, CH, 2, 1, W1, d0, d1);

    for (int c = 0; c < 2; c++)
    {
        for (int k = 0; k < NE; k++)
            d1[c * NE + k] = tanhf(d1[c * NE + k]);
        memcpy(m[c], &d1[c * NE], ERB1 * sizeof(float));
        for (int k = 0; k < NB - ERB1; k++)
        {
            float acc = 0.0f;
            for (int j = 0; j < ERB2; j++)
                acc += st->erb[j][k] * d1[c * NE + ERB1 + j];
            m[c][ERB1 + k] = acc;
        }
    }
    for (int k = 0; k < NB; k++)
    {
        float re = spec[2 * k], im = spec[2 * k + 1];
        out[2 * k] = re * m[0][k] - im * m[1][k];
        out[2 * k + 1] = im * m[0][k] + re * m[1][k];
    }
}

/* In-place radix-2 FFT of NFFT points; inverse is not scaled. */
static void fft(const gtcrn *st, float *re, float *im, int inverse)
{
    for (int i = 1, j = 0; i < NFFT; i++)
    {
        int bit = NFFT >> 1;
        for (; j & bit; bit >>= 1)
            j ^= bit;
        j ^= bit;
        if (i < j)
        {
            float t = re[i];
            re[i] = re[j];
            re[j] = t;
            t = im[i];
            im[i] = im[j];
            im[j] = t;
        }
    }
    for (int len = 2; len <= NFFT; len <<= 1)
    {
        int half = len >> 1, step = NFFT / len;
        for (int i = 0; i < NFFT; i += len)
            for (int k = 0; k < half; k++)
            {
                float wr = st->cos_t[k * step], wi = inverse ? st->sin_t[k * step] : -st->sin_t[k * step];
                float *ar = &re[i + k], *ai = &im[i + k], *br = &re[i + k + half], *bi = &im[i + k + half];
                float xr = *br * wr - *bi * wi, xi = *br * wi + *bi * wr;
                *br = *ar - xr;
                *bi = *ai - xi;
                *ar += xr;
                *ai += xi;
            }
    }
}

/* One hop: window, FFT, network, inverse FFT, overlap-add. */
static void stft_hop(gtcrn *st)
{
    float re[NFFT], im[NFFT], spec[2 * NB], enh[2 * NB];
    for (int n = 0; n < NFFT; n++)
    {
        re[n] = st->frame[n] * st->win[n];
        im[n] = 0.0f;
    }
    fft(st, re, im, 0);
    for (int k = 0; k < NB; k++)
    {
        spec[2 * k] = re[k];
        spec[2 * k + 1] = im[k];
    }
    gtcrn_frame(st, spec, enh);

    double ein = 0.0, eout = 0.0;
    for (int k = HB_LO; k < HB_HI; k++)
    {
        ein += spec[2 * k] * spec[2 * k] + spec[2 * k + 1] * spec[2 * k + 1];
        eout += enh[2 * k] * enh[2 * k] + enh[2 * k + 1] * enh[2 * k + 1];
    }
    st->hb_target = fminf(1.0f, (float)sqrt(eout / (ein + 1e-9)));

    for (int k = 0; k < NB; k++)
    {
        re[k] = enh[2 * k];
        im[k] = enh[2 * k + 1];
    }
    for (int k = NB; k < NFFT; k++)
    {
        re[k] = enh[2 * (NFFT - k)];
        im[k] = -enh[2 * (NFFT - k) + 1];
    }
    fft(st, re, im, 1);
    for (int n = 0; n < NFFT; n++)
        st->ola[n] += re[n] * (1.0f / NFFT) * st->win[n];

    memcpy(st->out16, st->ola, sizeof(st->out16));
    memcpy(st->ref16, st->frame, sizeof(st->ref16));
    memmove(st->ola, st->ola + HOP, (NFFT - HOP) * sizeof(float));
    memset(st->ola + NFFT - HOP, 0, HOP * sizeof(float));
    memmove(st->frame, st->frame + HOP, (NFFT - HOP) * sizeof(float));
}

static double bessel_i0(double x)
{
    double sum = 1.0, term = 1.0;
    for (int k = 1; k < 32; k++)
    {
        term *= (x / (2.0 * k)) * (x / (2.0 * k));
        sum += term;
    }
    return sum;
}

gtcrn *gtcrn_create(void)
{
    gtcrn *st = calloc(1, sizeof(*st));
    if (!st)
        return NULL;
    erb_init(st->erb);
    for (int n = 0; n < NFFT; n++)
        st->win[n] = (float)sqrt(0.5 - 0.5 * cos(2.0 * GTCRN_PI * n / NFFT));
    for (int k = 0; k < NFFT / 2; k++)
    {
        st->cos_t[k] = (float)cos(2.0 * GTCRN_PI * k / NFFT);
        st->sin_t[k] = (float)sin(2.0 * GTCRN_PI * k / NFFT);
    }
    /* Kaiser windowed sinc, beta 8, unity gain at DC. */
    double sum = 0.0, fc = FIR_HZ / 48000.0;
    for (int n = 0; n < FIR; n++)
    {
        double t = n - (FIR - 1) / 2.0, r = 2.0 * n / (FIR - 1) - 1.0;
        double s = t == 0.0 ? 2.0 * fc : sin(2.0 * GTCRN_PI * fc * t) / (GTCRN_PI * t);
        double v = s * bessel_i0(8.0 * sqrt(1.0 - r * r)) / bessel_i0(8.0);
        st->fir[n] = (float)v;
        sum += v;
    }
    for (int n = 0; n < FIR; n++)
        st->fir[n] = (float)(st->fir[n] / sum);
    gtcrn_reset(st);
    return st;
}

void gtcrn_destroy(gtcrn *st)
{
    free(st);
}

void gtcrn_reset(gtcrn *st)
{
    memset(st->cache, 0, sizeof(st->cache));
    memset(st->tra_h, 0, sizeof(st->tra_h));
    memset(st->inter_h, 0, sizeof(st->inter_h));
    memset(st->frame, 0, sizeof(st->frame));
    memset(st->ola, 0, sizeof(st->ola));
    memset(st->out16, 0, sizeof(st->out16));
    memset(st->ref16, 0, sizeof(st->ref16));
    memset(st->xin, 0, sizeof(st->xin));
    memset(st->up, 0, sizeof(st->up));
    memset(st->upref, 0, sizeof(st->upref));
    memset(st->xd, 0, sizeof(st->xd));
    st->fill = st->out_pos = st->phase = st->xd_pos = 0;
    st->hb_gain = st->hb_target = 1.0f;
}

void gtcrn_process(gtcrn *st, const float *in, float *out, float *dry, int n)
{
    const float hb_coef = 1.0f - expf(-1.0f / (48000.0f * 0.01f));
#ifdef __SSE__
    /* Flush denormals to zero: a quiet mic makes them, and Intel CPUs run 10x slower on them. */
    unsigned int csr = _mm_getcsr();
    _mm_setcsr(csr | 0x8040);
#endif
    for (int i = 0; i < n; i++)
    {
        memmove(st->xin + 1, st->xin, (FIR - 1) * sizeof(float));
        st->xin[0] = in[i];
        float x = st->xd[st->xd_pos];
        st->xd[st->xd_pos] = in[i];
        st->xd_pos = (st->xd_pos + 1) % GTCRN_DELAY;

        if (++st->phase == 3)
        {
            st->phase = 0;
            memmove(st->up + 1, st->up, (FIR / 3) * sizeof(float));
            memmove(st->upref + 1, st->upref, (FIR / 3) * sizeof(float));
            st->up[0] = st->out16[st->out_pos];
            st->upref[0] = st->ref16[st->out_pos];
            st->out_pos++;

            float s = 0.0f;
            for (int k = 0; k < FIR; k++)
                s += st->fir[k] * st->xin[k];
            st->frame[NFFT - HOP + st->fill] = s;
            if (++st->fill == HOP)
            {
                stft_hop(st);
                st->fill = 0;
                st->out_pos = 0;
            }
        }

        float y = 0.0f, r = 0.0f;
        for (int j = 0, k = st->phase; k < FIR; j++, k += 3)
        {
            y += st->fir[k] * st->up[j];
            r += st->fir[k] * st->upref[j];
        }
        st->hb_gain += hb_coef * (st->hb_target - st->hb_gain);
        out[i] = 3.0f * y + st->hb_gain * (x - 3.0f * r);
        dry[i] = x;
    }
#ifdef __SSE__
    _mm_setcsr(csr);
#endif
}
