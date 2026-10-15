rimworld := "/media/scarf/@steam/SteamLibrary/steamapps/common/RimWorld"
config := "/home/scarf/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"
steam_os := "RimWorldLinux_Data"
mod_name := "FcitxCjkInput"
package_id := "scarf.cjkinput"
mod_dest := rimworld + "/Mods/" + mod_name
native_src := "native/fcitxcjkinput.c"
native_test := "native/fcitxcjkinput_test.c"
native_bin := "1.7/Assemblies/libfcitxcjkinput.so"
native_cc := env_var_or_default("CC", "gcc")
native_pkg_config := env_var_or_default("PKG_CONFIG", "pkg-config")

# Download Harmony
prepare:
    curl -LO https://github.com/pardeike/HarmonyRimWorld/releases/latest/download/HarmonyMod.zip
    7z x -y HarmonyMod.zip -opackages

# Build the in-process native bridge
build-native:
    @command -v {{native_cc}} >/dev/null || { echo "C compiler not found; install gcc or set CC."; exit 1; }
    @command -v {{native_pkg_config}} >/dev/null || { echo "pkg-config not found; install pkgconf-pkg-config."; exit 1; }
    @{{native_pkg_config}} --exists dbus-1 || { echo "D-Bus development files missing; install dbus-devel."; exit 1; }
    mkdir -p "$(dirname {{native_bin}})"
    {{native_cc}} -std=gnu11 -shared -fPIC -fvisibility=hidden -pthread \
        -Wl,-soname,libfcitxcjkinput.so \
        -o {{native_bin}} {{native_src}} $({{native_pkg_config}} --cflags --libs dbus-1) \
        -Wall -Wextra -O2
    @echo "Native bridge built: {{native_bin}}"

# Format C# source
fmt:
    dotnet format ./Source/{{mod_name}}/{{mod_name}}.sln
    dotnet format ./Source/{{mod_name}}.Tests/{{mod_name}}.Tests.csproj

# Test native directional-key events
test-native:
    output="$(mktemp /tmp/fcitxcjkinput-test.XXXXXX)"; trap 'rm -f "$output"' 0; \
        {{native_cc}} -std=gnu11 -o "$output" {{native_test}} \
        $({{native_pkg_config}} --cflags --libs dbus-1) \
        -pthread -Wall -Wextra -Werror; "$output"

# Build the C# mod
build-dll:
    STEAM_APPS="{{rimworld}}/../.." \
    STEAM_OS="{{steam_os}}" \
    mise exec dotnet@8.0.422 -- dotnet build ./Source/{{mod_name}}/{{mod_name}}.csproj -c Release

# Run composition state-machine tests
test:
    mise exec dotnet@8.0.422 -- dotnet run --project Source/{{mod_name}}.Tests/{{mod_name}}.Tests.csproj -c Release

# Build everything
build: test test-native build-native build-dll

# Install built mod to RimWorld Mods directory
install: build
    @pgrep -x RimWorldLinux >/dev/null 2>&1 && { echo "RimWorld is running — 설치 전에 종료해주세요"; exit 1; } || true
    mkdir -p "{{mod_dest}}/1.7/Assemblies"
    mkdir -p "{{mod_dest}}/About"
    cp -r 1.7/Assemblies/* "{{mod_dest}}/1.7/Assemblies/"
    cp -r About/* "{{mod_dest}}/About/"
    rm -rf "{{mod_dest}}/helper" "{{mod_dest}}/Languages"
    @echo "Installed to {{mod_dest}}"

# Install and enable in ModsConfig.xml
enable: install
    @if [ -f "{{config}}" ]; then \
        if ! rg -q "{{package_id}}" "{{config}}"; then \
            sed -i 's|</activeMods>|  <li>{{package_id}}</li>\n</activeMods>|' "{{config}}"; \
            echo "Added {{package_id}} to activeMods"; \
        else \
            echo "{{package_id}} already in activeMods"; \
        fi; \
    else \
        echo "ModsConfig.xml not found at {{config}}"; \
    fi
