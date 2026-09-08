class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.5.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "7436ae63cdc19b7eecad58c9818c558ef1ce6bb9b5a4e4672776a1e0a8a17e41"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "849236a42e49a94ff55e815c1351c6254e8f3399a93ec79b68804f1682d98f6d"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "723a34e21de9aa0ba6d98e2e660df664f705fd53730907c3b06efcfeda815483"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "c038c1b7f1f80c3f6351d5758583812e32bb74cb669b6625f1dc80544c2f70ec"
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
