# G-Helper Linux package for NixOS.
#
# Builds ghelper from source as a Native AOT binary via buildDotnetModule.
# The C helpers the app embeds (ghelper-audio, wlr-randr, gpu-helper) are
# their own packages below; ghelper's preBuild copies the built binaries
# into the paths the csproj's EmbedNativeLibraries target expects.
{
  lib,
  stdenv,
  buildDotnetModule,
  dotnetCorePackages,
  # Build deps for ghelper (Native AOT link)
  clang,
  # Adds /run/opengl-driver/lib rpath (NVML dlopen)
  addDriverRunpath,
  # ghelper-audio build deps
  pkg-config,
  # wlr-randr build deps
  wayland-scanner,
  # Runtime deps for ghelper (Avalonia/SkiaSharp/ICU/.NET AOT)
  fontconfig,
  freetype,
  icu,
  openssl,
  zlib,
  libGL,
  libX11,
  libXcursor,
  libXi,
  libXrandr,
  libxkbcommon,
  wayland,
  dbus,
  glib,
  expat,
  pipewire,
  libxcb,
  libxext,
  libxinerama,
  libxfixes,
  libxrender,
  libICE,
  libSM,
}:

rec {
  # The main GUI binary, built from source as a Native AOT single binary.
  ghelper = buildDotnetModule rec {
    pname = "ghelper";
    version = "1.0.88";

    src = lib.cleanSource ../.;

    projectFile = "src/GHelper.Linux.csproj";
    nugetDeps = ./deps.json;

    dotnet-sdk = dotnetCorePackages.sdk_10_0;
    # AOT self-contained binary: no dotnet runtime needed.
    dotnet-runtime = null;

    runtimeId = "linux-x64";
    selfContainedBuild = true;
    executables = [ "ghelper" ];

    runtimeDeps = [
      fontconfig
      freetype
      icu
      openssl
      zlib
      libGL
      libX11
      libXcursor
      libXi
      libXrandr
      libxkbcommon
      wayland
      dbus
      glib
      expat
      pipewire
      libxcb
      libxext
      libxinerama
      libxfixes
      libxrender
      libICE
      libSM
      stdenv.cc.cc.lib # libstdc++
    ];

    nativeBuildInputs = [
      clang # Native AOT linker
    ];

    buildInputs = [
      stdenv.cc.cc.lib
      zlib
      icu
      openssl
      fontconfig
    ];

    preBuild = ''
      sed -i "s/VERSION_PLACEHOLDER/${version}/" install/90-ghelper.rules

      mkdir -p build/embedded
      cp ${ghelper-audio}/bin/ghelper-audio build/embedded/ghelper-audio
      cp ${wlr-randr}/bin/wlr-randr vendor/wlr-randr/wlr-randr
      cp ${gpu-helper}/bin/gpu-helper vendor/gpu-helper/gpu-helper
    '';

    postInstall = ''
      rm -f $out/lib/ghelper/ghelper.dbg

      # Native SkiaSharp/HarfBuzzSharp libraries from the restored NuGet
      # packages. Unlike build.sh (which embeds them into the single
      # portable binary), on Nix they are placed next to the binary in the
      # store: NativeLibExtractor's exe-dir lookup loads them from there
      # and their own deps come from the wrapper's LD_LIBRARY_PATH.
      # Restored packages live in NUGET_PACKAGES and/or the read-only
      # NUGET_FALLBACK_PACKAGES.
      for lib_spec in \
          "libSkiaSharp.so:skiasharp.nativeassets.linux" \
          "libHarfBuzzSharp.so:harfbuzzsharp.nativeassets.linux"; do
        lib_name="''${lib_spec%%:*}"
        pkg_name="''${lib_spec##*:}"
        so_path=$(find -L "$NUGET_PACKAGES" "$NUGET_FALLBACK_PACKAGES" \
                  -path "*/$pkg_name/*/runtimes/linux-x64/native/$lib_name" \
                  -print -quit 2>/dev/null)
        if [ -z "$so_path" ]; then
          echo "ERROR: $lib_name not found in restored NuGet packages" >&2
          exit 1
        fi
        install -m755 "$so_path" "$out/lib/ghelper/$lib_name"
      done

      # Desktop entry + icon (Exec=ghelper, resolved on PATH by the module).
      install -Dm644 ${../install/ghelper.desktop} \
        $out/share/applications/ghelper.desktop
      install -Dm644 ${../install/ghelper.png} \
        $out/share/icons/hicolor/256x256/apps/ghelper.png
    '';

    meta = {
      description = "G-Helper for Linux - ASUS/Lenovo laptop control utility";
      license = lib.licenses.gpl3;
      platforms = [ "x86_64-linux" ];
      mainProgram = "ghelper";
    };
  };

  # PipeWire audio helper (noise suppression + DSP chain) with vendored
  # rnnoise. Embedded into the ghelper binary, extracted at runtime by
  # NativeLibExtractor. Build mirrors audio-helper/Makefile.
  ghelper-audio = stdenv.mkDerivation {
    pname = "ghelper-audio";
    version = "1.0.0";

    src = ../audio-helper;

    nativeBuildInputs = [ pkg-config ];
    buildInputs = [ pipewire ];

    # Default make-based buildPhase (Makefile builds and strips ghelper-audio).

    installPhase = ''
      install -Dm755 ghelper-audio $out/bin/ghelper-audio
    '';

    meta = {
      description = "G-Helper PipeWire audio helper with rnnoise DSP";
      license = lib.licenses.gpl3;
      platforms = [ "x86_64-linux" ];
    };
  };

  # Wayland display tool (vendored, MIT license), used for refresh
  # rate switching. Embedded into the ghelper binary. Build mirrors build.sh.
  wlr-randr = stdenv.mkDerivation {
    pname = "ghelper-wlr-randr";
    version = lib.strings.trim (builtins.readFile ../vendor/wlr-randr/VERSION);

    src = ../vendor/wlr-randr;

    nativeBuildInputs = [ wayland-scanner ];
    buildInputs = [ wayland ];

    buildPhase = ''
      wayland-scanner client-header \
          protocol/wlr-output-management-unstable-v1.xml \
          wlr-output-management-unstable-v1-client-protocol.h
      wayland-scanner private-code \
          protocol/wlr-output-management-unstable-v1.xml \
          wlr-output-management-unstable-v1-protocol.c
      cc -O2 -o wlr-randr main.c wlr-output-management-unstable-v1-protocol.c \
          -I. -lwayland-client -lm
    '';

    installPhase = ''
      install -Dm755 wlr-randr $out/bin/wlr-randr
    '';

    meta = {
      description = "G-Helper vendored wlr-randr Wayland display tool";
      license = lib.licenses.mit;
      platforms = [ "x86_64-linux" ];
    };
  };

  # Privileged GPU helper, built from vendored C source.
  # Runs as root via sudo/pkexec - must be a native Nix binary
  # so the dynamic loader works without nix-ld in root context.
  # Build command mirrors build.sh. Ryzen SMU tuning is NOT built in;
  # the app uses the ryzenadj CLI (nixpkgs package) instead.
  gpu-helper = stdenv.mkDerivation {
    pname = "ghelper-gpu-helper";
    version = "1.0.0";

    src = ../vendor/gpu-helper;

    # dlopen("libnvidia-ml.so.1") resolves via the executable's RUNPATH on
    # NixOS, where the driver lives in /run/opengl-driver/lib.
    nativeBuildInputs = [ addDriverRunpath ];

    postFixup = ''
      addDriverRunpath $out/bin/gpu-helper
    '';

    buildPhase = ''
      cc -O2 -Wall -Wno-unused-result -DNDEBUG \
         -o gpu-helper gpu-helper.c \
         process_ops.c nvidia_ops.c pci_ops.c wmi_ops.c msr_ops.c \
         lenovo_ops.c \
         -ldl
    '';

    installPhase = ''
      install -Dm755 gpu-helper $out/bin/gpu-helper
    '';

    meta = {
      description = "G-Helper privileged GPU operations helper";
      license = lib.licenses.gpl3;
      platforms = [ "x86_64-linux" ];
    };
  };

  # GPU block helper bash script. Handles modprobe block files,
  # udev remove rules, and boot triggers for GPU mode persistence.
  gpu-block-helper = stdenv.mkDerivation {
    pname = "ghelper-gpu-block-helper";
    version = "1.0.0";

    src = ../install/gpu-block-helper.sh;
    dontUnpack = true;

    installPhase = ''
      install -Dm755 $src $out/bin/gpu-block-helper.sh
    '';

    meta = {
      description = "G-Helper GPU block helper script";
      license = lib.licenses.gpl3;
      platforms = [ "x86_64-linux" ];
    };
  };

  # GPU boot script: applies the pending GPU mode early in boot (firmware
  # dgpu_disable on ASUS, modprobe blacklist on PCI). Used by the optional
  # ghelper-gpu-boot.service. Standalone bash; calls gpu-helper/modprobe/udevadm.
  ghelper-gpu-boot = stdenv.mkDerivation {
    pname = "ghelper-gpu-boot";
    version = "1.0.0";

    src = ../install/ghelper-gpu-boot.sh;
    dontUnpack = true;

    installPhase = ''
      install -Dm755 $src $out/bin/ghelper-gpu-boot.sh
    '';

    meta = {
      description = "G-Helper GPU boot mode applier";
      license = lib.licenses.gpl3;
      platforms = [ "x86_64-linux" ];
    };
  };
}
