import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(5)

# Try to close popup
close_btns = driver.find_elements(webdriver.common.by.By.XPATH, "//*[@translate='Common_Closed' or contains(@ng-click, 'ok')] | //div[contains(text(), 'Đóng')] | //button[contains(text(), 'Đóng')]")
for b in close_btns:
    try: driver.execute_script('arguments[0].click();', b)
    except: pass
time.sleep(2)

# Click register
btns = driver.find_elements(webdriver.common.by.By.XPATH, "//button[contains(@ng-click, 'openRegister')]")
for b in btns:
    try: driver.execute_script('arguments[0].click();', b)
    except: pass
time.sleep(3)

# Print inputs
soup = BeautifulSoup(driver.page_source, 'html.parser')
inputs = soup.find_all('input')
for i in inputs:
    print('input:', i.attrs)

driver.quit()
