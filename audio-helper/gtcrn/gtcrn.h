/* GTCRN speech enhancement (Xiaobin Rong, MIT): the 16 kHz streaming model
 * in plain C, run on 48 kHz audio. Above ~7 kHz the input passes with a gain
 * taken from the model's 4..6 kHz suppression. */
#ifndef GTCRN_H
#define GTCRN_H

/* Output delay in 48 kHz samples (~35 ms). */
#define GTCRN_DELAY 1664

typedef struct gtcrn gtcrn;

gtcrn *gtcrn_create(void);
void gtcrn_destroy(gtcrn *st);
void gtcrn_reset(gtcrn *st);

/* Denoise n samples of 48 kHz audio in -1..1. out is in delayed by
 * GTCRN_DELAY; dry (may be NULL) gets in delayed by the same amount. */
void gtcrn_process(gtcrn *st, const float *in, float *out, float *dry, int n);

/* One 16 kHz STFT frame (257 bins, re/im interleaved) through the network. */
void gtcrn_frame(gtcrn *st, const float *spec, float *out);

#endif
