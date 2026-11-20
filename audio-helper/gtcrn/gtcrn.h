/* GTCRN 16 kHz speech model on 48 kHz audio; above ~7 kHz the input passes with the model's 4..6 kHz gain. */
#ifndef GTCRN_H
#define GTCRN_H

/* Output delay in 48 kHz samples (~35 ms). */
#define GTCRN_DELAY 1664

typedef struct gtcrn gtcrn;

gtcrn *gtcrn_create(void);
void gtcrn_destroy(gtcrn *st);
void gtcrn_reset(gtcrn *st);

/* Denoise n 48 kHz samples in -1..1; out (denoised) and dry (in) are both GTCRN_DELAY late. */
void gtcrn_process(gtcrn *st, const float *in, float *out, float *dry, int n);

#endif
