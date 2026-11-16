class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.9.0"
  license "MIT"

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "2026-09-10", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "e6373ed3aedea592269c77f5711f22b4b4d6b297c96e1c85d55fff3a710fc77a"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "0ad8e0d1000c119d29b634eaa8f841ca1de4ad22f89bffc873a6f3cb5367646a"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "2cb55fc0017109e03a1ba1be6c090a415835c8f13005455a60e4bf73ee502973"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "e9376483bf93817753663251f682d4a9592a209fd6d9f9c16e92298ca6b320e0"
    end
  end

  def install
    bin.install "elevate"
    generate_completions_from_executable(bin/"elevate", "completion")
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate --version")
  end
end
