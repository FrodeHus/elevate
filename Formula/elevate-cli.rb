class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.6.7"
  license "MIT"

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "2026-09-10", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "83411ab082be735f5aa434a16cec7df9706cfe55df61823e4cd80d504974eaac"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "cb6186d78ed7e7ff274ec32e1edc96213ddf2b4a8188c62e2d9e7973698eb167"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "219b635a5000ac2d9b74efe54776ee7ed02439700c6cc23dacc4bdadf81345bd"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "6aa75416631c8bb29d01a6294ebc69542cf9496c318380ae2530842f3682635e"
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
