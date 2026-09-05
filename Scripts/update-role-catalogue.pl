#!/usr/bin/env perl
# Regenerates Sources/ElevateCore/Resources/EntraBuiltInRoles.json from the
# markdown of https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference
# Usage: perl Scripts/update-role-catalogue.pl permissions-reference.md > Sources/ElevateCore/Resources/EntraBuiltInRoles.json
use strict; use warnings;
my @roles;
while (<>) {
    next unless /^\| (.+?) \| (.+?) \| ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) \|\s*$/;
    my ($name, $desc, $id) = ($1, $2, $3);
    my $priv = $desc =~ /privileged-label/ ? 'true' : 'false';
    $desc =~ s/\[!\[.*?\]\(.*?\)\]\(.*?\)//g;   # strip privileged badge markup
    $desc =~ s/\[(.*?)\]\(.*?\)/$1/g;            # unwrap links
    $desc =~ s/\s+$//; $desc =~ s/\\/\\\\/g; $desc =~ s/"/\\"/g;
    $name =~ s/"/\\"/g;
    push @roles, qq({"templateId":"$id","displayName":"$name","description":"$desc","isPrivileged":$priv});
}
print "[\n  " . join(",\n  ", @roles) . "\n]\n";
