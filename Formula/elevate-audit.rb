class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.6.6"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "1c3158b7b886cc8312e4eb4799a2bfdac87565f820ac4135d2704fe434b845b3"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "23205e5f74f3f258f3a4ddc6dafc2b0cb6cb1fc73605954f1c7cf7e354961b7d"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "b83a08af527e8c78e55ddd7e0a000f6c942f55fa5c29986c2bb95277d0b93562"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "8007fab92cdad899e00f1e90d3080ba9f9ac62399988397c18666fb17c0d303b"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
