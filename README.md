# SharpRES
Created by: Yamato Nagasaki

- A **CONCEPT** tool used for basic unpacking and repacking.
- It only supports single RES file though, not overall a batch/full unpacking and repacking
- This only supports base game only, DLC will not be supported. but information is provided in documenation


### Compatible with JP versions of the game:
- GOD EATER 2 PSP/Vita (NPJH50832 / PCSG00240)
- GOD EATER 2 RAGE BURST (PCSG00532)
- GOD EATER RESURRECTION (PCSG00719)
- GOD EATER OFFSHOT (PCSG00720 ~ PCSG00725)

### Files Required:
- package.rdp
- data.rdp (NOT PRESENT on RAGE BURST, RESURRECTION, OFFSHOT)
- patch.rdp (DLC CONTENT, NOT PRESENT on OFFSHOT)
- system.res (or any RES file).


## Localization releases and PC versions are not prioritized for the time being due to multiple languages
- PC, PS4, PSVita Localization has 6 languages (or 3 in some depends on game region)
- English, French, Italian, Deutsch, Espanol, Russian. So i can't work on these ones very well.
- For PC Version, check GECV.


## Credits:
HaoJun/Randerion's GECV: BLZ4 Codes and other structure references
- https://github.com/HaoJun0823/GECV-OLD/



### Program's arguments (Functions)
Note: Put a RES file within the same directory as the executable
- use `-x` for extraction
- use `-r` for repacking
- use `-E` for enforcement (used for debugging). 
- Enforcement is used to swap extrernal source's fileset RDP offsets to SET_C or SET_D masking based on population. or swap with one external source to another.. 



# Demonstration Images (used Bullet.res for sampling)
### Original
<img width="1095" height="737" alt="image" src="https://github.com/user-attachments/assets/361af702-b138-48fc-bfbf-35648014fff5" />

### Modified
<img width="893" height="810" alt="image" src="https://github.com/user-attachments/assets/af67f736-97df-417b-9539-d0d650a2f09c" />

### PSVita Version
<img width="960" height="544" alt="2025-08-10-093256-750782" src="https://github.com/user-attachments/assets/9cf1946a-3821-4fce-b2cb-fe8ea3b60c5f" />


#### Documentation about the RES File
- Not accurate but the TXT file should give you proper context about it
[TXT Documentation](https://github.com/nachotacos69/Sharp_EATER/blob/main/GOD%20EATER%20(RES%20JP)%20Structure%20PSP%2BVita.txt)
[Classes](https://github.com/nachotacos69/Sharp_EATER/blob/main/Classes.md)
