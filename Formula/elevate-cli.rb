class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.6.4"
  license "MIT"

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "2026-09-10", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "94f31952108c3fae01094cd91abaf824148acd3656063de22e81ae98b55fe504"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "be6b231a9dcddfc4d750fd5590375b0ca1ca4f26899842bbca8e8b072059d060"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "a70fc420d2f5be188a8e32b6975c4ef9040d67d6e37d03fba56f053944ea01b8"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "55b7ee6addd84f46297a88a63a617a07d98df4fae7d61135e5cee68903d5dea1"
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
