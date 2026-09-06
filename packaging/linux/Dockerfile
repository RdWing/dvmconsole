# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

FROM debian:12-slim@sha256:88200866dfff7ea7f5cbcb6ec7c8a701889efe6fe859fe64d6990e4b07ea4171

ARG RUST_TOOLCHAIN=1.85.0
ARG RUSTUP_VERSION=1.28.2
ARG DOTNET_SDK_VERSION=10.0.400
ARG DEBIAN_SNAPSHOT=20260901T000000Z
ARG TARGETARCH

LABEL org.opencontainers.image.title="DVM Console portable Linux builder" \
      org.opencontainers.image.description="Debian 12 ABI baseline for Linux preview packages"

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    RUSTUP_HOME=/opt/rustup \
    CARGO_HOME=/opt/cargo \
    DOTNET_ROOT=/opt/dotnet \
    PATH=/opt/dotnet:/opt/cargo/bin:${PATH}

RUN printf '%s\n' \
        'Types: deb' \
        "URIs: http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}" \
        'Suites: bookworm bookworm-updates' \
        'Components: main' \
        'Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg' \
        '' \
        'Types: deb' \
        "URIs: http://snapshot.debian.org/archive/debian-security/${DEBIAN_SNAPSHOT}" \
        'Suites: bookworm-security' \
        'Components: main' \
        'Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg' \
        > /etc/apt/sources.list.d/debian.sources \
    && printf 'Acquire::Check-Valid-Until "false";\n' \
        > /etc/apt/apt.conf.d/99snapshot \
    && apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates \
    && sed -i 's|URIs: http://snapshot.debian.org|URIs: https://snapshot.debian.org|g' \
        /etc/apt/sources.list.d/debian.sources \
    && apt-get update \
    && apt-get install --yes --no-install-recommends \
        binutils \
        build-essential \
        cmake \
        curl \
        file \
        fonts-dejavu-core \
        gpg \
        libfontconfig1 \
        libgl1 \
        libice6 \
        libicu72 \
        libpipewire-0.3-dev \
        libsm6 \
        libssl3 \
        libx11-6 \
        libxcursor1 \
        libxext6 \
        libxi6 \
        libxrandr2 \
        libxrender1 \
        libxtst6 \
        pkg-config \
        python3 \
        unzip \
        wget \
        xauth \
        xvfb \
        zip \
        zlib1g \
    && case "${TARGETARCH}" in \
        amd64) \
            rustup_arch=x86_64-unknown-linux-gnu; \
            rustup_sha=20a06e644b0d9bd2fbdbfd52d42540bdde820ea7df86e92e533c073da0cdd43c; \
            dotnet_arch=x64; \
            dotnet_sha=1033977dd837150e0814cf0c5d5b17ceb63925fda7ba2158b47258a4bd7c048cf82eac3bc1166f3146f53124a3f5fba09db1de1260d2ce96399860303b404b48 ;; \
        arm64) \
            rustup_arch=aarch64-unknown-linux-gnu; \
            rustup_sha=e3853c5a252fca15252d07cb23a1bdd9377a8c6f3efa01531109281ae47f841c; \
            dotnet_arch=arm64; \
            dotnet_sha=a1b45da58e5591fff909a6126ac6bfc1ef9c12bc72c0625f7815e83a82be1a902317ee96926cbbf81324a45c6abf2ed8102a216d0507879cc166159af78d1b77 ;; \
        *) printf 'Unsupported rustup architecture: %s\n' "${TARGETARCH}" >&2; exit 2 ;; \
      esac \
    && curl --proto '=https' --tlsv1.2 --fail --show-error --location \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${dotnet_arch}.tar.gz" \
        --output /tmp/dotnet-sdk.tar.gz \
    && printf '%s  %s\n' "${dotnet_sha}" /tmp/dotnet-sdk.tar.gz | sha512sum --check \
    && mkdir -p /opt/dotnet \
    && tar -xzf /tmp/dotnet-sdk.tar.gz -C /opt/dotnet \
    && rm /tmp/dotnet-sdk.tar.gz \
    && curl --proto '=https' --tlsv1.2 --fail --show-error --location \
        "https://static.rust-lang.org/rustup/archive/${RUSTUP_VERSION}/${rustup_arch}/rustup-init" \
        --output /tmp/rustup-init \
    && printf '%s  %s\n' "${rustup_sha}" /tmp/rustup-init | sha256sum --check \
    && chmod 755 /tmp/rustup-init \
    && /tmp/rustup-init \
        -y \
        --no-modify-path \
        --profile minimal \
        --default-toolchain "${RUST_TOOLCHAIN}" \
        --component clippy \
        --component rustfmt \
    && rm /tmp/rustup-init \
    && chmod --recursive a+rX /opt/cargo /opt/rustup \
    && rm --recursive --force /var/lib/apt/lists/*

# Use immutable AppImage tool/runtime releases and verify both before they
# become part of the release toolchain. Extracting appimagetool once avoids a
# FUSE dependency inside unprivileged build containers.
RUN case "${TARGETARCH}" in \
        amd64) \
            appimage_arch=x86_64; \
            tool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0; \
            runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;; \
        arm64) \
            appimage_arch=aarch64; \
            tool_sha=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158; \
            runtime_sha=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;; \
        *) printf 'Unsupported AppImage builder architecture: %s\n' "${TARGETARCH}" >&2; exit 2 ;; \
    esac \
    && curl --proto '=https' --tlsv1.2 --fail --show-error --location \
        "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-${appimage_arch}.AppImage" \
        --output /tmp/appimagetool.AppImage \
    && printf '%s  %s\n' "${tool_sha}" /tmp/appimagetool.AppImage | sha256sum --check \
    && chmod 755 /tmp/appimagetool.AppImage \
    && mkdir -p /opt/appimagetool \
    && cd /tmp \
    && /tmp/appimagetool.AppImage --appimage-extract >/dev/null \
    && cp -R /tmp/squashfs-root/. /opt/appimagetool/ \
    && curl --proto '=https' --tlsv1.2 --fail --show-error --location \
        "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-${appimage_arch}" \
        --output /opt/appimage-runtime \
    && printf '%s  %s\n' "${runtime_sha}" /opt/appimage-runtime | sha256sum --check \
    && chmod 644 /opt/appimage-runtime \
    && rm -rf /tmp/appimagetool.AppImage /tmp/squashfs-root

WORKDIR /work
