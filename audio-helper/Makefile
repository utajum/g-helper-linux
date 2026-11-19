# G-Helper audio helper build.
# Produces a single self-contained ELF `ghelper-audio` which is then embedded
# in the main ghelper AOT binary by build.sh.

CC      ?= cc
STRIP   ?= strip

CFLAGS  ?= -O2 -flto -fno-strict-aliasing -Wall -Wextra
CFLAGS  += -I. -DRNNOISE_BUILD

PW_CFLAGS = $(shell pkg-config --cflags libpipewire-0.3)
PW_LIBS   = $(shell pkg-config --libs libpipewire-0.3)

LDLIBS  = $(PW_LIBS) -lm -lpthread
LDFLAGS = -flto

RNN_SRCS = rnnoise/denoise.c rnnoise/rnn.c rnnoise/rnn_data.c \
           rnnoise/rnn_reader.c rnnoise/pitch.c rnnoise/kiss_fft.c \
           rnnoise/celt_lpc.c

RNN_OBJS = $(RNN_SRCS:.c=.o)
GT_OBJS = gtcrn/gtcrn.o

all: ghelper-audio

ghelper-audio: main.o $(RNN_OBJS) $(GT_OBJS)
	$(CC) $(LDFLAGS) -o $@ $^ $(LDLIBS)
	$(STRIP) $@

main.o: main.c protocol.h rnnoise/rnnoise.h gtcrn/gtcrn.h
	$(CC) $(CFLAGS) $(PW_CFLAGS) -c main.c -o $@

rnnoise/%.o: rnnoise/%.c
	$(CC) $(CFLAGS) -c $< -o $@

gtcrn/gtcrn.o: gtcrn/gtcrn.c gtcrn/gtcrn.h gtcrn/gtcrn_weights.h
	$(CC) $(CFLAGS) -c $< -o $@

# Offline renderer for tests: same DSP chain, file in, file out.
render: tests/offline/render.c main.c protocol.h rnnoise/rnnoise.h $(RNN_OBJS) $(GT_OBJS)
	$(CC) $(CFLAGS) $(PW_CFLAGS) $(LDFLAGS) -o $@ tests/offline/render.c $(RNN_OBJS) $(GT_OBJS) $(LDLIBS)

clean:
	rm -f main.o $(RNN_OBJS) $(GT_OBJS) ghelper-audio render

.PHONY: all clean
