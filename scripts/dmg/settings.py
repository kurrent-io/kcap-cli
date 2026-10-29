# dmgbuild settings; build-dmg.sh passes the bundle path as -D app=... and the directory as -D here=...
import os.path

app = defines["app"]
here = defines["here"]
app_name = os.path.basename(app)

format = "UDZO"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = os.path.join(here, "..", "..", "src", "Capacitor.App", "Assets", "kcap-icon.icns")
background = os.path.join(here, "background.tiff")

# Must match the gap the arrow in background.svg is drawn across.
icon_locations = {app_name: (170, 220), "Applications": (470, 220)}
# The frame, title bar included, around the 640x480 background. A Finder showing its tab, path and
# status bars covers ~124pt of it, so the artwork keeps everything above y=384.
window_rect = ((200, 120), (640, 508))
default_view = "icon-view"
icon_size = 112
text_size = 13
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
