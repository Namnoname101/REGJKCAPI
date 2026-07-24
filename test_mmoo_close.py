import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from selenium.webdriver.common.by import By
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(5)

# Try to find and click 'Đóng'
try:
    close_btns = driver.find_elements(By.XPATH, "//*[contains(text(), 'Đóng') or @translate='Common_Closed' or contains(@ng-click, 'ok')]")
    print(f'Found {len(close_btns)} close buttons')
    for b in close_btns:
        driver.execute_script('arguments[0].click();', b)
    time.sleep(2)
except Exception as e:
    print('Error closing:', e)

# See if popup is still there
try:
    title = driver.find_elements(By.XPATH, "//*[contains(text(), 'TIN TỨC MỚI NHẤT')]")
    print(f'Popup still there? {len(title) > 0}')
except:
    pass

driver.quit()
