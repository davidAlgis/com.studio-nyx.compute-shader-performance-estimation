import pandas as pd
import matplotlib.pyplot as plt

# Path to the CSV file
csv_file = '2024-01-19T16_51_04.csv'

# Reading the CSV file
# Assuming no header in the CSV file
data = pd.read_csv(csv_file, header=None, sep=';')

# Extracting columns for plotting
x = data[0]
y = data[1]
print(x)
print(y)
# Creating the scatter plot
plt.scatter(x, y)

# Adding labels and title (optional)
plt.xlabel('Frame')
plt.ylabel('Time (ms)')
plt.title('Execution time by frame')

# Displaying the plot
plt.show()
