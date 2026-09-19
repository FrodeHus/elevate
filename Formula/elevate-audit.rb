class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/FrodeHus/elevate"
  version "1.1.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "b8da5f0753cdbe6536bc5977331e9cfda54de374cc9389c9f9f9e3774b55c2e0"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "5a74a4d754ce03f0f82058c665d7717b256a82fd0b9ca5011465bab55623e53d"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "84b92d3abac945d06c7dd34258097cedfbe85be71af85b3663e31eb7dfb85b49"
    end
    on_intel do
      url "https://github.com/FrodeHus/elevate/releases/download/audit-v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "aead58fb6d4000b3787b8cf9f6a0e37cb85fc00f4e400399cc5534592d511a72"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
