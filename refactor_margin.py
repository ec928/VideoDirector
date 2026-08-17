import sys

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Views\DirectorPlayerControl.xaml", "r", encoding="utf-8") as f:
    text = f.read()

text = text.replace('AreTransportControlsEnabled="False" Stretch="UniformToFill"', 
                    'AreTransportControlsEnabled="False" Stretch="UniformToFill" Margin="-1"')

with open(r"c:\Users\chan_\OneDrive\Apps\0-Development\VideoDirector\Views\DirectorPlayerControl.xaml", "w", encoding="utf-8") as f:
    f.write(text)
