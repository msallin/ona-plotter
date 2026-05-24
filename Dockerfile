# syntax=docker/dockerfile:1.7
#
# Multi-stage build for OnaPlotter as a standalone web server. Two
# AOTs in play - WASM AOT compiles the Blazor client to WebAssembly
# (slow), NativeAOT compiles the Kestrel host to a self-contained
# native binary. Final image lands around 35 to 50 MB depending on
# arch and WASM payload size, on top of the ~10 MB Alpine
# runtime-deps base.
#
# Image layout: /app/OnaPlotter.Server (the native binary) and
# /app/wwwroot (the Blazor static bundle). The host reads SK_SERVER_URL
# and BASE_HREF env vars at startup and templates wwwroot/appsettings.json
# and wwwroot/index.html before the first request - see
# OnaPlotter.Server/Program.cs ApplyRuntimeConfig.
#
# Run:
#   docker run -p 8080:8080 \
#     -e SK_SERVER_URL=https://my-sk:3000 \
#     ghcr.io/msallin/ona-plotter:<version>

ARG DOTNET_VERSION=10.0

# -------- Build stage --------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build

# NativeAOT toolchain prerequisites on Alpine. The .NET SDK image
# ships without a native C toolchain so we install it here once.
# build-base pulls gcc + binutils + libc-dev; clang + lld give
# NativeAOT a usable linker; zlib-dev is the one runtime dep the
# linker actually wants. nodejs + npm satisfy the esbuild step
# (the MinifyPublishedJs MSBuild target in OnaPlotter.csproj runs
# `npx esbuild` against the JS interop layer before the bundle is
# copied to /app/publish).
RUN apk add --no-cache build-base clang lld zlib-dev nodejs npm

WORKDIR /src

# JS deps first - small layer, rarely changes once esbuild + lint
# tooling pins are stable. Restored before .NET so an esbuild bump
# doesn't bust the dotnet restore cache.
COPY package.json package-lock.json ./
RUN npm ci --omit=optional

# .NET restore next, again as its own layer. Copying just the
# project files (not sources) lets a code change reuse the restored
# package cache.
COPY OnaPlotter.slnx global.json ./
COPY OnaPlotter/OnaPlotter.csproj OnaPlotter/
COPY OnaPlotter.Server/OnaPlotter.Server.csproj OnaPlotter.Server/
RUN dotnet workload install wasm-tools \
 && dotnet restore OnaPlotter.Server/OnaPlotter.Server.csproj

# Source. Order keeps the layer cache: only this and later layers
# rebuild on a source change.
COPY OnaPlotter/ OnaPlotter/
COPY OnaPlotter.Server/ OnaPlotter.Server/

# Publish. Hits both AOTs in one invocation:
#   1. OnaPlotter (the WASM client) - IL to WebAssembly,
#      InvariantGlobalization + WasmStripILAfterAOT + Speed
#      optimisation from its own csproj.
#   2. OnaPlotter.Server (the Kestrel host) - IL to native code
#      for the container arch, single-file self-contained.
# Both projects' AOT settings already gate on Configuration=Release,
# so this is the only switch needed.
RUN dotnet publish OnaPlotter.Server/OnaPlotter.Server.csproj \
    -c Release \
    -o /app/publish

# -------- Runtime stage --------
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-alpine

WORKDIR /app

# --chown so the non-root user can write templated wwwroot files
# at container start. ApplyRuntimeConfig mutates appsettings.json
# and index.html from env vars; both live under wwwroot.
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# runtime-deps:alpine sets APP_UID=1654 as a non-root user. Reuse
# it rather than running as root.
USER $APP_UID

# Native binary; no `dotnet` prefix because PublishAot produces a
# self-contained executable.
ENTRYPOINT ["./OnaPlotter.Server"]
